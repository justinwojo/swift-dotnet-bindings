// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for complex type projections — verifies each projection produces
/// correct parameter plans, return plans, element conversions, and type metadata.
/// </summary>
public class ComplexProjectionTests
{
    #region ExistentialProjection

    [Fact]
    public void Existential_WellKnown_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "Swift.AnyError", proxyClassName: null);
        Assert.Equal("Swift.AnyError", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_ProxyWrapped_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IImageProcessing", "ImageProcessingProxy");
        Assert.Equal("IImageProcessing", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_Unknown_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        Assert.Equal("object", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer0", proj.PInvokeType);
    }

    [Fact]
    public void Existential_ParameterPlan_ExtractsContainer()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetParameterPlan("item");
        Assert.Contains("ISwiftExistentialConvertible", plan.PInvokeExpression);
        Assert.Contains("GetExistentialContainer", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_Proxy_ConstructsProxy()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("new DescribableProxy(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_WellKnown_ConstructsType()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "Swift.AnyError", proxyClassName: null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("new Swift.AnyError(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_Object_PassThrough()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ElementConversion_Proxy()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var paramConv = proj.GetParameterElementConversion("e");
        Assert.NotNull(paramConv);
        Assert.Contains("GetExistentialContainer", paramConv);

        var retConv = proj.GetReturnElementConversion("e");
        Assert.NotNull(retConv);
        Assert.Equal("new DescribableProxy(e)", retConv);
    }

    [Fact]
    public void Existential_ElementConversion_Object_ReturnCastsToObject()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        var retConv = proj.GetReturnElementConversion("e");
        Assert.Equal("(object)e", retConv);
    }

    [Fact]
    public void Existential_DoesNotRequireSwiftWrapper()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region ArrayProjection

    [Fact]
    public void Array_BlittableElement_ReturnType()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        Assert.Equal("IReadOnlyList<Int64>", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Array_BlittableElement_ParamType()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: true);
        Assert.Equal("IEnumerable<Int64>", proj.PublicType);
    }

    [Fact]
    public void Array_StringElement_Types()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        Assert.Equal("IReadOnlyList<string>", proj.PublicType);
    }

    [Fact]
    public void Array_ParamPlan_NoConversion_HasUsing()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("items");

        Assert.Equal("itemsBuffer", plan.PInvokeExpression);
        // Should have Using for SwiftArray and PayloadBuffer
        Assert.True(plan.SetupStatements.Count >= 2);
    }

    [Fact]
    public void Array_ParamPlan_WithConversion_HasSelectAndDisposal()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("names");

        Assert.Equal("namesBuffer", plan.PInvokeExpression);
        // Should have Select conversion line
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("new SwiftString", firstLine.Code);

        // Should have try/finally for disposal since StringProjection.ElementRequiresDisposal=true
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void Array_ParamPlan_EnumElement_DirectFromEnumerable()
    {
        // Enums are blittable — no element conversion needed. Direct FromEnumerable.
        var elem = new SimpleEnumProjection("Direction", "int");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("dirs");

        Assert.Equal("dirsBuffer", plan.PInvokeExpression);
        // No Select — enums pass directly to FromEnumerable
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FromEnumerable(dirs)", firstLine.Code);
        Assert.DoesNotContain(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void Array_ReturnPlan_Direct_RequiresUnsafe()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.Contains("new IntPtr(&result)", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(e => e)", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_IndirectResult_NoUnsafe()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.DoesNotContain("new IntPtr(&", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_StringElement_HasConversionLambda()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(e =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_AsyncCallback_IsPassThrough()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.AsyncCallback);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_DoesNotRequireSwiftWrapper()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), false);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Array_ExposesElementProjection()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, false);
        Assert.Same(elem, proj.ElementProjection);
    }

    #endregion

    #region DictionaryProjection

    [Fact]
    public void Dictionary_StringString_Types()
    {
        var key = new StringProjection();
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        Assert.Equal("IReadOnlyDictionary<string, string>", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Dictionary_ParamType()
    {
        var key = new StringProjection();
        var val = new BlittableProjection("Int64");
        var proj = new DictionaryProjection(key, val, isParameter: true);
        Assert.Equal("IDictionary<string, Int64>", proj.PublicType);
    }

    [Fact]
    public void Dictionary_BlittableBlittable_ParamPlan_NoConversion()
    {
        var key = new BlittableProjection("Int64");
        var val = new BlittableProjection("double");
        var proj = new DictionaryProjection(key, val, isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);
        // No Select conversion needed — first stmt is container creation, then Using + PayloadBuffer
        var firstSetup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FromDictionary", firstSetup.Code);
    }

    [Fact]
    public void Dictionary_StringString_ParamPlan_HasConversionAndDisposal()
    {
        var key = new StringProjection();
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("new SwiftString", firstLine.Code);
    }

    [Fact]
    public void Dictionary_ReturnPlan_Direct_RequiresUnsafe()
    {
        var key = new BlittableProjection("Int64");
        var val = new BlittableProjection("double");
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.Contains("SwiftDictionary", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ReturnPlan_StringValue_HasConversionLambda()
    {
        var key = new BlittableProjection("Int64");
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Blittable key → no key conversion, string value → 1-arg overload
        Assert.Contains(".AsProjected(v =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ReturnPlan_StringKeyEnumValue()
    {
        var key = new StringProjection();
        var val = new SimpleEnumProjection("Direction", "int");
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // String key has conversion (ToString), enum value has no conversion (blittable passthrough)
        Assert.Contains(".AsProjected(k =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ExposesKeyAndValueProjections()
    {
        var key = new StringProjection();
        var val = new BlittableProjection("Int64");
        var proj = new DictionaryProjection(key, val, false);
        Assert.Same(key, proj.KeyProjection);
        Assert.Same(val, proj.ValueProjection);
    }

    #endregion

    #region OptionalProjection

    [Fact]
    public void Optional_Blittable_Types()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        Assert.Equal("Int64?", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Optional_String_Types()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        Assert.Equal("string?", proj.PublicType);
    }

    [Fact]
    public void Optional_ParamPlan_SimpleInner_InlineTernary()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetParameterPlan("val");

        Assert.Equal("valBuffer", plan.PInvokeExpression);
        // Simple inner (no element conversion) → Using with ternary
        var firstSetup = plan.SetupStatements[0];
        var usingStmt = Assert.IsType<MarshalStatement.Using>(firstSetup);
        Assert.Contains("SwiftOptional", usingStmt.Type);
        Assert.Contains("NewSome", usingStmt.InitExpression);
        Assert.Contains("NewNone", usingStmt.InitExpression);
    }

    [Fact]
    public void Optional_ParamPlan_ComplexInner_HasBranching()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var plan = proj.GetParameterPlan("name");

        Assert.Equal("nameBuffer", plan.PInvokeExpression);
        // Complex inner (has element conversion) → Block if/else
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header.Contains("if ("));
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "else");
    }

    [Fact]
    public void Optional_ReturnPlan_Direct_RequiresUnsafe()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.Contains("SwiftOptional", plan.PInvokeExpression);
        Assert.Contains("ToNullable()", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_IndirectResult_NoUnsafe()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("ToNullable()", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_StringInner_HasTwoStepConversion()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Two-step: ToNullable() first, then conditional conversion
        Assert.NotEmpty(plan.SetupStatements);
        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("ToNullable()", setupLine.Code);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_ExistentialInner_DiscriminantCheck()
    {
        var inner = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var proj = new OptionalProjection(inner, isExistentialInner: true);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("new DescribableProxy", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ExposesInnerProjection()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        Assert.Same(inner, proj.InnerProjection);
    }

    [Fact]
    public void Optional_ParamPlan_ArrayInner_UsesSwiftArrayNotIntPtr()
    {
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: true);
        var proj = new OptionalProjection(arrayProj);
        var plan = proj.GetParameterPlan("items");

        Assert.Equal("itemsBuffer", plan.PInvokeExpression);
        // SwiftOptional should use SwiftArray<Int64>, not IntPtr
        var allCode = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
        var allUsings = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type));
        var combined = allCode + "\n" + allUsings;
        Assert.Contains("SwiftOptional<SwiftArray<Int64>>", combined);
        Assert.DoesNotContain("SwiftOptional<IntPtr>", combined);
    }

    [Fact]
    public void Optional_ParamPlan_DictionaryInner_UsesSwiftDictionaryNotIntPtr()
    {
        var dictProj = new DictionaryProjection(
            new StringProjection(), new BlittableProjection("Int64"), isParameter: true);
        var proj = new OptionalProjection(dictProj);
        var plan = proj.GetParameterPlan("data");

        Assert.Equal("dataBuffer", plan.PInvokeExpression);
        var allCode = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
        var allUsings = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type));
        var combined = allCode + "\n" + allUsings;
        Assert.Contains("SwiftOptional<SwiftDictionary<SwiftString, Int64>>", combined);
        Assert.DoesNotContain("SwiftOptional<IntPtr>", combined);
    }

    [Fact]
    public void Optional_ReturnPlan_ArrayInner_UsesContainerConversion()
    {
        var arrayProj = new ArrayProjection(new StringProjection(), isParameter: false);
        var proj = new OptionalProjection(arrayProj);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Should use discriminant check + AsProjected, not ToNullable
        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_DictionaryInner_UsesContainerConversion()
    {
        var dictProj = new DictionaryProjection(
            new BlittableProjection("Int64"), new StringProjection(), isParameter: false);
        var proj = new OptionalProjection(dictProj);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
    }

    #endregion

    #region TupleProjection

    [Fact]
    public void Tuple_AllBlittable_Types()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });

        Assert.Equal("(Int64, double)", proj.PublicType);
        Assert.Equal("ValueTuple<Int64, double>", proj.PInvokeType);
    }

    [Fact]
    public void Tuple_MixedTypes()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });

        Assert.Equal("(string, Int64)", proj.PublicType);
        Assert.Equal("ValueTuple<SwiftString, Int64>", proj.PInvokeType);
    }

    [Fact]
    public void Tuple_AllBlittable_ParamPlan_IsPassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });
        var plan = proj.GetParameterPlan("t");
        Assert.Equal("t", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Tuple_MixedTypes_ParamPlan_HasConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetParameterPlan("t");

        // Should have setup line for string conversion
        Assert.NotEmpty(plan.SetupStatements);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("new SwiftString", firstLine.Code);
    }

    [Fact]
    public void Tuple_AllBlittable_ReturnPlan_IsPassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Tuple_MixedTypes_ReturnPlan_HasPerElementConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // Should have setup for string element conversion
        Assert.NotEmpty(plan.SetupStatements);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("elem0", firstLine.Code);
        Assert.Contains("ToString()", firstLine.Code);

        // Final expression should use converted elem0 and raw Item2
        Assert.Contains("elem0", plan.PInvokeExpression);
        Assert.Contains("result.Item2", plan.PInvokeExpression);
    }

    [Fact]
    public void Tuple_ExposesElementProjections()
    {
        var elems = new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new StringProjection()
        };
        var proj = new TupleProjection(elems);
        Assert.Equal(2, proj.ElementProjections.Count);
        Assert.Same(elems[0], proj.ElementProjections[0]);
        Assert.Same(elems[1], proj.ElementProjections[1]);
    }

    [Fact]
    public void Tuple_PInvokeAttribute_IsNull()
    {
        var proj = new TupleProjection(new ITypeProjection[] { new BlittableProjection("Int64") });
        Assert.Null(proj.PInvokeAttribute);
    }

    #endregion

    #region ClosureProjection

    [Fact]
    public void Closure_Action_Types()
    {
        var proj = new ClosureProjection(
            Array.Empty<ITypeProjection>(),
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("Action", proj.PublicType);
        Assert.Equal("SwiftClosureData", proj.PInvokeType);
    }

    [Fact]
    public void Closure_ActionWithArgs_Types()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection(), new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("Action<string, Int64>", proj.PublicType);
    }

    [Fact]
    public void Closure_Func_Types()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection() },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("Func<string, bool>", proj.PublicType);
    }

    [Fact]
    public void Closure_NonEscaping_PInvokeType_IsFuncPtr()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: false,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Contains("delegate* unmanaged[Swift]", proj.PInvokeType);
    }

    [Fact]
    public void Closure_Escaping_ParamPlan_HasGCHandle()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var plan = proj.GetParameterPlan("handler");

        Assert.Contains("GCHandle.Alloc", plan.SetupStatements.OfType<MarshalStatement.Line>().First().Code);
        Assert.Contains("SwiftClosureData", plan.SetupStatements.OfType<MarshalStatement.Line>().Last().Code);
        Assert.Equal("handlerClosure", plan.PInvokeExpression);

        // Should have finally cleanup
        Assert.NotEmpty(plan.CleanupStatements);
        var finallyBlock = Assert.IsType<MarshalStatement.Block>(plan.CleanupStatements[0]);
        Assert.Equal("finally", finallyBlock.Header);
    }

    [Fact]
    public void Closure_ReturnPlan_HasLambdaBody()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        // Should check for null function pointer
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FunctionPointer == IntPtr.Zero", firstLine.Code);
    }

    [Fact]
    public void Closure_Escaping_HasCallbackDeclarations()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var callbacks = proj.CallbackDeclarations;
        Assert.Single(callbacks);
        Assert.Equal("testCallback", callbacks[0].MethodName);
        Assert.Equal("CallConvCdecl", callbacks[0].CallingConvention);
        Assert.NotNull(callbacks[0].StaticFieldDeclaration);
        Assert.Contains("s_testCallback", callbacks[0].StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_NonEscaping_NoCallbackDeclarations()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: false,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Empty(proj.CallbackDeclarations);
    }

    [Fact]
    public void Closure_Callback_HasDelegateExtraction()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection() },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "onComplete");

        var callbacks = proj.CallbackDeclarations;
        var cb = callbacks[0];
        var bodyCode = string.Join("\n", cb.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("GetDelegateFromContext", bodyCode);
        // Args should be reverse-converted (P/Invoke → delegate types)
        Assert.Contains("ToString()", bodyCode);
    }

    [Fact]
    public void Closure_ReturnPlan_WithConvertedArg_IncludesConversionInLambda()
    {
        // NonFrozenStruct arg has element conversion — the lambda body should include it
        var argProj = new NonFrozenStructProjection("Pipeline");
        var proj = new ClosureProjection(
            new ITypeProjection[] { argProj },
            returnProjection: new BoolProjection(),
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "pipelineCb");

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // The lambda body should contain the conversion variable
        var lambdaLine = plan.SetupStatements.OfType<MarshalStatement.Line>()
            .FirstOrDefault(l => l.Code.Contains("closureResult"));
        Assert.NotNull(lambdaLine);
        Assert.Contains("arg0Converted", lambdaLine!.Code);
        Assert.Contains("DangerousGetHandle", lambdaLine.Code);
    }

    [Fact]
    public void Closure_VoidCallback_StaticField_HasVoidReturn()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "voidCb");

        var callback = proj.CallbackDeclarations[0];
        Assert.NotNull(callback.StaticFieldDeclaration);
        // delegate* should end with ", void>" for void callback — last type arg is return type
        Assert.Contains(", void>", callback.StaticFieldDeclaration!);
        // Static field should be initialized with method address
        Assert.Contains("= &voidCb;", callback.StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_NonVoidCallback_StaticField_HasReturnType()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "boolCb");

        var callback = proj.CallbackDeclarations[0];
        Assert.NotNull(callback.StaticFieldDeclaration);
        // delegate* should end with return type
        Assert.Contains(", bool>", callback.StaticFieldDeclaration!);
        // Static field should be initialized with method address
        Assert.Contains("= &boolCb;", callback.StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_DoesNotRequireSwiftWrapper()
    {
        var proj = new ClosureProjection(
            Array.Empty<ITypeProjection>(), null, true, false, false, "cb");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region AsyncProjection

    [Fact]
    public void Async_TaskT_Types()
    {
        var inner = new StringProjection();
        var proj = new AsyncProjection(inner, throws: true, callbackPrefix: "test");
        Assert.Equal("Task<string>", proj.PublicType);
        Assert.Equal("void", proj.PInvokeType);
    }

    [Fact]
    public void Async_Task_VoidReturn_Types()
    {
        var proj = new AsyncProjection(innerReturnProjection: null, throws: false, callbackPrefix: "test");
        Assert.Equal("Task", proj.PublicType);
        Assert.Equal("void", proj.PInvokeType);
    }

    [Fact]
    public void Async_RequiresSwiftWrapper()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        Assert.True(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Async_GetSwiftWrapperCode_NotNull()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$s10TestModule9fetchDatayys5Int64VYaKF",
            ModuleName = "TestModule",
            MethodName = "fetchData",
            OriginalCallExpression = "TestModule.fetchData()"
        });
        Assert.NotNull(code);
        Assert.Contains("@_silgen_name", code);
        Assert.Contains("$s10TestModule9fetchDatayys5Int64VYaKF_async", code);
        Assert.Contains("Task {", code);
        Assert.Contains("callback", code);
        Assert.Contains("errorCallback", code);
        Assert.Contains("_SBWTaskEntry", code);
        Assert.Contains("defer {", code);
        Assert.Contains("TestModule.fetchData()", code);
        Assert.Contains("CancellationError", code);
    }

    [Fact]
    public void Async_SwiftWrapper_NonThrowing_NoTryCatch()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            ModuleName = "TestModule",
            MethodName = "fetchData",
            OriginalCallExpression = "fetchData()"
        });
        Assert.NotNull(code);
        Assert.DoesNotContain("do {", code);
        Assert.DoesNotContain("catch", code);
        Assert.DoesNotContain("errorCallback", code);
        Assert.Contains("await fetchData()", code);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesMethodNameFallback()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            ModuleName = "TestModule",
            MethodName = "fetchData"
        });
        Assert.NotNull(code);
        // Without OriginalCallExpression, falls back to MethodName()
        Assert.Contains("await fetchData()", code);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesMangledNameForSilgenName()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$s4Test6myFuncyyYaF",
            ModuleName = "Test",
            MethodName = "myFunc"
        });
        Assert.NotNull(code);
        Assert.Contains("@_silgen_name(\"$s4Test6myFuncyyYaF_async\")", code);
    }

    [Fact]
    public void Async_ReturnPlan_AsyncCallback_HasTCS()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var plan = proj.GetReturnPlan("handle", ReturnStrategy.AsyncCallback);

        Assert.NotEmpty(plan.SetupStatements);
        var tcsLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("TaskCompletionSource", tcsLine.Code);

        var holderLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[1]);
        Assert.Contains("new object[]", holderLine.Code);
    }

    [Fact]
    public void Async_CallbackDeclarations_Throwing_HasSuccessAndError()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var callbacks = proj.CallbackDeclarations;

        Assert.Equal(2, callbacks.Count);
        Assert.Equal("testSuccessCallback", callbacks[0].MethodName);
        Assert.Equal("testErrorCallback", callbacks[1].MethodName);
    }

    [Fact]
    public void Async_CallbackDeclarations_NonThrowing_SuccessOnly()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var callbacks = proj.CallbackDeclarations;

        Assert.Single(callbacks);
        Assert.Equal("testSuccessCallback", callbacks[0].MethodName);
    }

    [Fact]
    public void Async_SuccessCallback_HasResultConversion()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var callback = proj.CallbackDeclarations[0];

        Assert.Contains("SwiftString rawResult", callback.Signature);
        var bodyCode = string.Join("\n", callback.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("TrySetResult", bodyCode);
        Assert.Contains("ToString()", bodyCode);
    }

    [Fact]
    public void Async_ErrorCallback_HasExceptionCreation()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var errorCallback = proj.CallbackDeclarations[1];

        var bodyCode = string.Join("\n", errorCallback.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("TrySetException", bodyCode);
        Assert.Contains("OperationCanceledException", bodyCode);
        // Should marshal the error message pointer to string first
        Assert.Contains("PtrToStringUTF8", bodyCode);
        Assert.Contains("SwiftException(errorMessage)", bodyCode);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesSwiftTypeNames()
    {
        // Int64 should map to Int64 in Swift, not remain as C# type
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$sTest",
            ModuleName = "Test",
            MethodName = "fetch",
            OriginalCallExpression = "fetch()"
        });
        Assert.NotNull(code);
        Assert.Contains("Int64, Int64", code); // return param + task param in callback
    }

    [Fact]
    public void Async_SwiftWrapper_UsesContextSwiftCallbackReturnType()
    {
        // When SwiftCallbackReturnType is provided, it should be used instead of mapping
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$sTest",
            ModuleName = "Test",
            MethodName = "getName",
            OriginalCallExpression = "getName()",
            SwiftCallbackReturnType = "String"
        });
        Assert.NotNull(code);
        Assert.Contains("String, Int64", code); // String from context, Int64 for task
    }

    [Fact]
    public void Async_ExposesInnerProjection()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new AsyncProjection(inner, false, "test");
        Assert.Same(inner, proj.InnerReturnProjection);
    }

    #endregion

    #region Element Conversion Defaults

    [Fact]
    public void Blittable_ElementConversions_AreNull()
    {
        ITypeProjection proj = new BlittableProjection("Int64");
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void Bool_ElementConversions_AreNull()
    {
        ITypeProjection proj = new BoolProjection();
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void String_ElementConversions()
    {
        var proj = new StringProjection();
        Assert.Equal("new SwiftString(e)", proj.GetParameterElementConversion("e"));
        Assert.Equal("e.ToString()", proj.GetReturnElementConversion("e"));
        Assert.True(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void SimpleEnum_ElementConversions_AreNull()
    {
        // Enums are blittable — no element conversion needed inside containers.
        // Standalone parameter/return plans handle the cast to/from underlying type.
        var proj = new SimpleEnumProjection("Direction", "int");
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void ObjCBridged_ElementConversions()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        Assert.Equal("e.Handle", proj.GetParameterElementConversion("e"));
        Assert.Equal("ObjCRuntime.Runtime.GetNSObject<UIImage>(e)!", proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void NonFrozenStruct_ElementConversions()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        Assert.Equal("e.Payload.DangerousGetHandle()", proj.GetParameterElementConversion("e"));
        // Return element conversion is null — when used inside Optional, ToNullable() handles
        // construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
        Assert.Null(proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void NativeRemapped_ElementConversions()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        Assert.Equal("new SwiftURL(e)", proj.GetParameterElementConversion("e"));
        // MarshalFromSwiftType = _swiftWrapperType, so container elements are already
        // the wrapper type — just call the conversion method directly (no re-wrapping).
        Assert.Equal("e.ToNSUrl()", proj.GetReturnElementConversion("e"));
        Assert.True(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void NativeRemapped_InArray_ParamPlan_UsesFromFactoryMethod()
    {
        // Array<Foundation.NSUrl> parameter — element conversion should use FromNSUrl factory method
        var elem = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("urls");

        Assert.Equal("urlsBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("SwiftURL.FromNSUrl", firstLine.Code);
        // Should have disposal since NativeRemapped.ElementRequiresDisposal = true
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void NativeRemapped_InArray_ReturnPlan_UsesToConversionMethod()
    {
        // Array<Foundation.NSUrl> return — element conversion should use ToNSUrl
        var elem = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(e =>", plan.PInvokeExpression);
        Assert.Contains("ToNSUrl()", plan.PInvokeExpression);
        // Must NOT contain the namespace-qualified fallback "ToFoundation.NSUrl"
        Assert.DoesNotContain("ToFoundation", plan.PInvokeExpression);
    }

    #endregion

    #region CallbackDeclaration Defaults

    [Fact]
    public void SimpleProjections_HaveNoCallbackDeclarations()
    {
        ITypeProjection proj = new BlittableProjection("Int64");
        Assert.Empty(proj.CallbackDeclarations);

        proj = new StringProjection();
        Assert.Empty(proj.CallbackDeclarations);

        proj = new BoolProjection();
        Assert.Empty(proj.CallbackDeclarations);
    }

    #endregion
}
