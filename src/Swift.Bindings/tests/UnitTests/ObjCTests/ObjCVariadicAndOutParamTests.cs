// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for variadic method detection, value-type pointer (out) parameter handling,
/// and correct emission of [Internal]/IsVariadic attributes and out params.
/// </summary>
public class ObjCVariadicAndOutParamTests
{
    private const string HeadersPath = DefaultHeadersPath;

    // ─────────────────────────────────────────────
    // Fix #2: Variadic method detection (ClangAstParser)
    // ─────────────────────────────────────────────

    [Fact]
    public void Parse_VariadicMethod_SetsIsVariadicTrue()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MOSResults",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "objectsWhere:",
                    "instance": true,
                    "variadic": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "predicateFormat",
                            "type": { "qualType": "NSString *" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var method = Assert.Single(module.Classes[0].Methods);
        Assert.True(method.IsVariadic);
        Assert.Equal("objectsWhere:", method.Selector);
        Assert.Single(method.Parameters);
    }

    [Fact]
    public void Parse_NonVariadicMethod_SetsIsVariadicFalse()
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
                    "name": "doWork",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var method = Assert.Single(module.Classes[0].Methods);
        Assert.False(method.IsVariadic);
    }

    [Fact]
    public void Parse_VariadicFunction_SetsIsVariadicTrue()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "TLFormatString",
            "variadic": true,
            "type": { "qualType": "void (NSString *, ...)" },
            "inner": [
                {
                    "kind": "ParmVarDecl",
                    "name": "format",
                    "type": { "qualType": "NSString *" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var func = Assert.Single(module.Functions);
        Assert.True(func.IsVariadic);
        Assert.Equal("TLFormatString", func.Name);
    }

    // ─────────────────────────────────────────────
    // Fix #2: Variadic method emission (ApiDefinitionEmitter)
    // ─────────────────────────────────────────────

    [Fact]
    public void Emit_VariadicMethod_HasInternalAndIsVariadic()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("MOSResults", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "indexOfObjectWhere:",
                    ReturnType = SimpleType("NSUInteger"),
                    IsInstanceMethod = true,
                    IsVariadic = true,
                    Parameters = [new ObjCParameterDecl { Name = "predicateFormat", Type = SimpleType("NSString", isPointer: true) }]
                }))
            .Build();

        var result = EmitApiDefinition(module);

        // Should have [Internal] attribute
        Assert.Contains("[Internal]", result);
        // Should have IsVariadic = true in Export
        Assert.Contains("[Export(\"indexOfObjectWhere:\", IsVariadic = true)]", result);
        // Should have IntPtr varArgs parameter
        Assert.Contains("IntPtr varArgs", result);
        // Should still have the original parameter
        Assert.Contains("string predicateFormat", result);
    }

    [Fact]
    public void Emit_NonVariadicMethod_NoInternalOrIsVariadic()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("Widget", configure: c => c
                .Method("doWork", "void"))
            .Build();

        var result = EmitApiDefinition(module);

        Assert.DoesNotContain("[Internal]", result);
        Assert.DoesNotContain("IsVariadic", result);
        Assert.DoesNotContain("IntPtr varArgs", result);
    }

    [Fact]
    public void Emit_VariadicMethodNoParams_HasOnlyVarArgs()
    {
        // Edge case: variadic method with no named params (unusual but possible)
        var module = ObjCModuleBuilder.Create()
            .WithClass("Logger", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "log",
                    ReturnType = SimpleType("void"),
                    IsInstanceMethod = true,
                    IsVariadic = true,
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("[Internal]", result);
        Assert.Contains("[Export(\"log\", IsVariadic = true)]", result);
        Assert.Contains("IntPtr varArgs", result);
    }

    // ─────────────────────────────────────────────
    // Fix #3: Value type pointer → out param (ObjCTypeMapper)
    // ─────────────────────────────────────────────

    [Fact]
    public void IsValueTypePointerParameter_BoolPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "BOOL", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_CGPointPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "CGPoint", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_CGRectPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "CGRect", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_IntPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "int", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_NSIntegerPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "NSInteger", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_CGFloatPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "CGFloat", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_NSObjectPointer_ReturnsFalse()
    {
        // NSObject * is an ObjC object reference, not a value type pointer
        var typeRef = new ObjCTypeRef { Name = "NSObject", IsPointer = true };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_NSStringPointer_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef { Name = "NSString", IsPointer = true };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_VoidPointer_ReturnsFalse()
    {
        // void * → IntPtr, not an out param
        var typeRef = new ObjCTypeRef { Name = "void", IsPointer = true };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_DoublePointer_ReturnsFalse()
    {
        // NSError ** has PointeeType set, should not be treated as value-type out
        var typeRef = new ObjCTypeRef
        {
            Name = "NSError",
            IsPointer = true,
            PointeeType = new ObjCTypeRef { Name = "NSError", IsPointer = true }
        };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_NonPointer_ReturnsFalse()
    {
        // Non-pointer BOOL is not an out param
        var typeRef = new ObjCTypeRef { Name = "BOOL" };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_IdProtocol_ReturnsFalse()
    {
        // id<Proto> * is a protocol-qualified object ref, not a value-type out param
        var typeRef = new ObjCTypeRef
        {
            Name = "id",
            IsPointer = true,
            ProtocolQualifications = ["NSCoding"]
        };
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_CLLocationCoordinate2DPointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "CLLocationCoordinate2D", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    [Fact]
    public void IsValueTypePointerParameter_NSRangePointer_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "NSRange", IsPointer = true };
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef));
    }

    // ─────────────────────────────────────────────
    // Fix #3: Out param emission (ApiDefinitionEmitter)
    // ─────────────────────────────────────────────

    [Fact]
    public void Emit_BoolPointerParam_EmitsOutBool()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("Printer", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "getLabelSize:",
                    ReturnType = SimpleType("NSInteger"),
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "succeeded",
                        Type = new ObjCTypeRef { Name = "BOOL", IsPointer = true }
                    }]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("out bool succeeded", result);
        // Should NOT contain by-value bool
        Assert.DoesNotContain("bool succeeded)", result.Replace("out bool succeeded", ""));
    }

    [Fact]
    public void Emit_CGPointPointerParam_EmitsOutCGPoint()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("PathFinder", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "findClosestPoint:",
                    ReturnType = SimpleType("void"),
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "closest",
                        Type = new ObjCTypeRef { Name = "CGPoint", IsPointer = true }
                    }]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("out CGPoint closest", result);
    }

    [Fact]
    public void Emit_NSObjectPointerParam_DoesNotEmitOut()
    {
        // Normal ObjC object pointer should NOT become out
        var module = ObjCModuleBuilder.Create()
            .WithClass("Manager", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "processObject:",
                    ReturnType = SimpleType("void"),
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "obj",
                        Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true }
                    }]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("NSObject obj", result);
        Assert.DoesNotContain("out NSObject", result);
    }

    [Fact]
    public void Emit_MixedParamsWithOutParam_EmitsCorrectly()
    {
        // Method with both normal and out params
        var module = ObjCModuleBuilder.Create()
            .WithClass("Converter", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "convertValue:result:",
                    ReturnType = SimpleType("BOOL"),
                    IsInstanceMethod = true,
                    Parameters =
                    [
                        new ObjCParameterDecl
                        {
                            Name = "input",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        },
                        new ObjCParameterDecl
                        {
                            Name = "result",
                            Type = new ObjCTypeRef { Name = "CGRect", IsPointer = true }
                        }
                    ]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("string input, out CGRect result", result);
    }

    // ─────────────────────────────────────────────
    // Fix #14: P/Invoke pointer params (StructsAndEnumsEmitter)
    // ─────────────────────────────────────────────

    [Fact]
    public void EmitFunction_CGPointPointerParam_EmitsOutParam()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLFindClosestPoint",
                ReturnType = SimpleType("void"),
                Parameters =
                [
                    new ObjCParameterDecl
                    {
                        Name = "closest1",
                        Type = new ObjCTypeRef { Name = "CGPoint", IsPointer = true }
                    }
                ]
            })
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("out CGPoint closest1", result);
    }

    [Fact]
    public void EmitFunction_BoolPointerParam_EmitsOutBool()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLCheckStatus",
                ReturnType = SimpleType("int"),
                Parameters =
                [
                    new ObjCParameterDecl
                    {
                        Name = "status",
                        Type = new ObjCTypeRef { Name = "BOOL", IsPointer = true }
                    }
                ]
            })
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("out bool status", result);
    }

    [Fact]
    public void EmitFunction_VariadicFunction_IsSkipped()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLLogMessage",
                ReturnType = SimpleType("void"),
                IsVariadic = true,
                Parameters =
                [
                    new ObjCParameterDecl
                    {
                        Name = "format",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                    }
                ]
            })
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.DoesNotContain("TLLogMessage", result);
    }

    [Fact]
    public void EmitFunction_NSObjectPointerParam_NotOutParam()
    {
        // Normal object pointer in a function should NOT become out
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLProcessObject",
                ReturnType = SimpleType("void"),
                Parameters =
                [
                    new ObjCParameterDecl
                    {
                        Name = "obj",
                        Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true }
                    }
                ]
            })
            .Build();

        var result = EmitStructsAndEnums(module);
        // NSObject pointer in a function would be mapped by MapType, not as out
        Assert.DoesNotContain("out NSObject", result);
    }

    // ─────────────────────────────────────────────
    // Edge cases
    // ─────────────────────────────────────────────

    [Fact]
    public void MapValueTypePointerParameterType_Bool_ReturnsBool()
    {
        var typeRef = new ObjCTypeRef { Name = "BOOL", IsPointer = true };
        Assert.Equal("bool", ObjCTypeMapper.MapValueTypePointerParameterType(typeRef));
    }

    [Fact]
    public void MapValueTypePointerParameterType_CGFloat_ReturnsNfloat()
    {
        var typeRef = new ObjCTypeRef { Name = "CGFloat", IsPointer = true };
        Assert.Equal("nfloat", ObjCTypeMapper.MapValueTypePointerParameterType(typeRef));
    }

    [Fact]
    public void MapValueTypePointerParameterType_CGPoint_ReturnsCGPoint()
    {
        var typeRef = new ObjCTypeRef { Name = "CGPoint", IsPointer = true };
        Assert.Equal("CGPoint", ObjCTypeMapper.MapValueTypePointerParameterType(typeRef));
    }

    [Fact]
    public void Emit_VariadicProtocolMethod_HasInternalAndIsVariadic()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol("Queryable", p => p
                .Method(new ObjCMethodDecl
                {
                    Selector = "objectsWhere:",
                    ReturnType = SimpleType("NSObject", isPointer: true),
                    IsInstanceMethod = true,
                    IsOptional = true,
                    IsVariadic = true,
                    Parameters = [new ObjCParameterDecl { Name = "query", Type = SimpleType("NSString", isPointer: true) }]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("[Internal]", result);
        Assert.Contains("[Export(\"objectsWhere:\", IsVariadic = true)]", result);
        Assert.Contains("IntPtr varArgs", result);
    }

    // ─────────────────────────────────────────────
    // Fix #3 (P1): Typedef/enum out-param in categories
    // ─────────────────────────────────────────────

    [Fact]
    public void Emit_CategoryMethod_TypedefEnumOutParam_EmitsOutParam()
    {
        // A category method with an enum pointer parameter should emit
        // "out MyEnum paramName" even through the category emission path.
        var module = ObjCModuleBuilder.Create()
            .WithEnum(new ObjCEnumDecl
            {
                Name = "MyErrorCode",
                Cases = [new ObjCEnumCaseDecl { Name = "MyErrorCodeNone", Value = 0 }],
                UnderlyingType = SimpleType("NSInteger")
            })
            .WithClass(new ObjCClassDecl { Name = "MyClass" })
            .WithCategory(new ObjCCategoryDecl
            {
                CategoryName = "Validation",
                ClassName = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "validateWithError:",
                    ReturnType = SimpleType("BOOL"),
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "error",
                        Type = new ObjCTypeRef { Name = "MyErrorCode", IsPointer = true }
                    }]
                }]
            })
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("out MyErrorCode error", result);
    }

    [Fact]
    public void EmitFunction_TypedefEnumOutParam_EmitsOutParam()
    {
        // A C function with an enum pointer parameter should emit "out" in StructsAndEnums.
        var module = ObjCModuleBuilder.Create()
            .WithEnum(new ObjCEnumDecl
            {
                Name = "StatusCode",
                Cases = [new ObjCEnumCaseDecl { Name = "StatusCodeOK", Value = 0 }],
                UnderlyingType = SimpleType("NSUInteger")
            })
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "GetStatus",
                ReturnType = SimpleType("void"),
                Parameters = [new ObjCParameterDecl
                {
                    Name = "status",
                    Type = new ObjCTypeRef { Name = "StatusCode", IsPointer = true }
                }]
            })
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("out StatusCode status", result);
    }

    [Fact]
    public void Emit_TypedefAliasToEnumPointer_EmitsOutParam()
    {
        // typedef MyEnum MyEnumAlias; MyEnumAlias *value → out MyEnumAlias value
        var module = ObjCModuleBuilder.Create()
            .WithEnum(new ObjCEnumDecl
            {
                Name = "MyErrorCode",
                Cases = [new ObjCEnumCaseDecl { Name = "MyErrorCodeNone", Value = 0 }],
                UnderlyingType = SimpleType("NSInteger")
            })
            .WithTypedef("MyErrorAlias", "MyErrorCode")
            .WithClass("MyClass", configure: c => c
                .Method(new ObjCMethodDecl
                {
                    Selector = "validateWithError:",
                    ReturnType = SimpleType("BOOL"),
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "error",
                        Type = new ObjCTypeRef { Name = "MyErrorAlias", IsPointer = true }
                    }]
                }))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("out", result);
    }
}
