// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for all 8 hard case compositions from the architecture retrospective supplement.
/// These verify that projections compose correctly for real-world complex type scenarios.
/// </summary>
public class CompositeProjectionTests
{
    #region Case 1: Dictionary<String, String> parameter — multi-statement disposal

    [Fact]
    public void Case1_DictionaryStringString_Param_HasMultiStatementDisposal()
    {
        var key = new StringProjection();
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);

        // Should have Select conversion
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(kvp =>", firstLine.Code);

        // Should have try/finally with disposal for both key and value
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    #endregion

    #region Case 2: Optional<Dictionary<String, Array<String>>> parameter — 3 nesting levels

    [Fact]
    public void Case2_OptionalDictArrayString_Param_ThreeNestingLevels()
    {
        // Build from inside out: String → Array<String> → Dictionary<String, Array<String>> → Optional
        var stringProj = new StringProjection();
        var arrayProj = new ArrayProjection(stringProj, isParameter: true);
        var dictProj = new DictionaryProjection(stringProj, arrayProj, isParameter: true);
        var optionalProj = new OptionalProjection(dictProj);

        // Verify composed types
        Assert.Equal("IDictionary<string, IEnumerable<string>>?", optionalProj.PublicType);
        Assert.Equal("IntPtr", optionalProj.PInvokeType);

        // Parameter plan should have branching (complex inner)
        var plan = optionalProj.GetParameterPlan("data");
        Assert.Equal("dataBuffer", plan.PInvokeExpression);

        // Should have if/else blocks for Optional null check
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header.Contains("if ("));
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "else");
    }

    #endregion

    #region Case 3: String return with IndirectResult — MarshalFromSwift

    [Fact]
    public void Case3_StringReturn_IndirectResult_MarshalFromSwift()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.False(plan.RequiresUnsafe);
    }

    #endregion

    #region Case 4: Optional<Existential> return — discriminant check + proxy

    [Fact]
    public void Case4_OptionalExistential_Return_DiscriminantCheck()
    {
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IImageProcessing", "ImageProcessingProxy");
        var proj = new OptionalProjection(inner, isExistentialInner: true);

        // Type
        Assert.Equal("IImageProcessing?", proj.PublicType);

        // Return plan
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Should use discriminant check (not ToNullable)
        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("new ImageProcessingProxy", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
    }

    #endregion

    #region Case 5: Closure return with NonFrozenStruct arg — VWT lambda body

    [Fact]
    public void Case5_ClosureReturn_NonFrozenStructArg_HasVWTLambda()
    {
        var argProj = new NonFrozenStructProjection("ImagePipeline");
        var retProj = new BoolProjection();
        var proj = new ClosureProjection(
            new ITypeProjection[] { argProj },
            retProj,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "pipelineCallback");

        // Public type should be Func with correct types
        Assert.Equal("Func<ImagePipeline, bool>", proj.PublicType);

        // Return plan should be a lambda
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.True(plan.RequiresUnsafe);

        // Should check for null function pointer
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FunctionPointer == IntPtr.Zero", firstLine.Code);
    }

    #endregion

    #region Case 6: Array<Existential> parameter — per-element container extraction

    [Fact]
    public void Case6_ArrayExistential_Param_PerElementContainerExtraction()
    {
        var innerExist = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var proj = new ArrayProjection(innerExist, isParameter: true);

        Assert.Equal("IEnumerable<IDescribable>", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);

        var plan = proj.GetParameterPlan("items");
        Assert.Equal("itemsBuffer", plan.PInvokeExpression);

        // Should have Select with container extraction
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("GetExistentialContainer", firstLine.Code);
    }

    #endregion

    #region Case 7: Async tuple (String, Int) return — Swift wrapper + per-element callback marshalling

    [Fact]
    public void Case7_AsyncTupleReturn_SwiftWrapper_PerElementMarshalling()
    {
        var tupleProj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var asyncProj = new AsyncProjection(tupleProj, throws: true, callbackPrefix: "fetchData");

        // Type
        Assert.Equal("Task<(string, Int64)>", asyncProj.PublicType);
        Assert.Equal("void", asyncProj.PInvokeType);

        // Requires Swift wrapper
        Assert.True(asyncProj.RequiresSwiftWrapper);

        // Swift wrapper code
        var code = asyncProj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$s10TestModule9fetchDatayySS_s5Int64VtYaKF",
            ModuleName = "TestModule",
            MethodName = "fetchData",
            OriginalCallExpression = "TestModule.fetchData()"
        });
        Assert.NotNull(code);
        Assert.Contains("@_silgen_name", code);
        Assert.Contains("Task {", code);
        Assert.Contains("TestModule.fetchData()", code);

        // Callback declarations — success + error for throwing
        var callbacks = asyncProj.CallbackDeclarations;
        Assert.Equal(2, callbacks.Count);
        Assert.Equal("fetchDataSuccessCallback", callbacks[0].MethodName);
        Assert.Equal("fetchDataErrorCallback", callbacks[1].MethodName);
    }

    #endregion

    #region Case 8: Optional<Existential> parameter — conditional container extraction

    [Fact]
    public void Case8_OptionalExistential_Param_ConditionalExtraction()
    {
        var innerExist = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IImageProcessing", "ImageProcessingProxy");
        var proj = new OptionalProjection(innerExist, isExistentialInner: true);

        Assert.Equal("IImageProcessing?", proj.PublicType);

        var plan = proj.GetParameterPlan("processor");
        Assert.Equal("processorBuffer", plan.PInvokeExpression);

        // Should have if/else branching (existential has element conversion)
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header.Contains("if ("));

        // The some branch should contain existential container extraction
        var ifBlock = plan.SetupStatements.OfType<MarshalStatement.Block>()
            .First(b => b.Header.Contains("if ("));
        var someLine = Assert.IsType<MarshalStatement.Line>(ifBlock.Body[0]);
        Assert.Contains("GetExistentialContainer", someLine.Code);
    }

    #endregion

    #region Container-in-Optional composition — verifies SwiftOptional uses container types

    [Fact]
    public void OptionalArray_Param_SwiftOptionalUsesSwiftArray()
    {
        // Optional<Array<Int64>> — SwiftOptional must use SwiftArray<Int64>, not IntPtr
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: true);
        var optProj = new OptionalProjection(arrayProj);
        var plan = optProj.GetParameterPlan("nums");

        var allCode = string.Join("\n",
            plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).Concat(
            plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type + " " + u.InitExpression)));
        Assert.Contains("SwiftOptional<SwiftArray<Int64>>", allCode);
    }

    [Fact]
    public void OptionalArray_Return_UsesDiscriminantAndAsProjected()
    {
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var optProj = new OptionalProjection(arrayProj);
        var plan = optProj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Container inner → discriminant check (not ToNullable)
        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
    }

    #endregion

    #region Deferred limitations — skip tests to prevent silent regression

    [Fact(Skip = "Deferred: Optional<Optional<T>> not used by any validated library. Inner OptionalProjection.ContainerTypeName defaults to IntPtr.")]
    public void NestedOptionalOptional_IsKnownLimitation()
    {
        // Optional<Optional<Int64>> — outer should use SwiftOptional<SwiftOptional<Int64>>
        // but inner OptionalProjection.ContainerTypeName is IntPtr (the default),
        // so outer incorrectly gets SwiftOptional<IntPtr>.
        var inner = new OptionalProjection(new BlittableProjection("Int64"));
        var outer = new OptionalProjection(inner);
        var plan = outer.GetParameterPlan("val");

        var allCode = string.Join("\n",
            plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).Concat(
            plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type)));
        // When fixed, this should contain SwiftOptional<SwiftOptional<Int64>>, not SwiftOptional<IntPtr>
        Assert.Contains("SwiftOptional<SwiftOptional<Int64>>", allCode);
    }

    #endregion

    #region Additional composition edge cases

    [Fact]
    public void Nested_ArrayOfOptionalString()
    {
        // Array<Optional<String>> — element projection is Optional<String>
        var optStr = new OptionalProjection(new StringProjection());
        var proj = new ArrayProjection(optStr, isParameter: false);
        Assert.Equal("IReadOnlyList<string?>", proj.PublicType);
    }

    [Fact]
    public void DictionaryWithEnumKeyAndArrayValue()
    {
        var enumProj = new SimpleEnumProjection("Direction", "int");
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var dictProj = new DictionaryProjection(enumProj, arrayProj, isParameter: false);

        Assert.Equal("IReadOnlyDictionary<Direction, IReadOnlyList<Int64>>", dictProj.PublicType);
    }

    [Fact]
    public void AsyncWithOptionalStringReturn()
    {
        var optStr = new OptionalProjection(new StringProjection());
        var asyncProj = new AsyncProjection(optStr, throws: false, callbackPrefix: "fetch");

        Assert.Equal("Task<string?>", asyncProj.PublicType);
        Assert.True(asyncProj.RequiresSwiftWrapper);
    }

    [Fact]
    public void ClosureWithEnumArg_FallbackCast_UsesUnderlyingType()
    {
        // Closure with SimpleEnum arg — IsCastablePInvokeType guard allows the cast
        // because enum underlying types (int) are safe to cast, unlike IntPtr (classes).
        var enumProj = new SimpleEnumProjection("Status", "int");
        var closureProj = new ClosureProjection(
            new ITypeProjection[] { enumProj },
            returnProjection: new BlittableProjection("Int64"),
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "enumCb");

        // Callback body: PInvoke arg (int) should be cast to public type (Status)
        var callbacks = closureProj.CallbackDeclarations;
        var bodyCode = string.Join("\n", callbacks[0].Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("(Status)", bodyCode);

        // Return plan (closure invoker): public arg (Status) should be cast to PInvoke type (int)
        var plan = closureProj.GetReturnPlan("result", ReturnStrategy.Direct);
        var lambdaLine = plan.SetupStatements.OfType<MarshalStatement.Line>()
            .FirstOrDefault(l => l.Code.Contains("closureResult"));
        Assert.NotNull(lambdaLine);
        Assert.Contains("(int)", lambdaLine!.Code);
    }

    [Fact]
    public void ClosureWithClassArg_NoCast_IntPtrExcluded()
    {
        // Closure with class arg (PInvokeType=IntPtr) — IsCastablePInvokeType
        // must NOT insert a cast, because (MyClass)someIntPtr is invalid C#.
        var classProj = new ClassProjection("MyViewController");
        var closureProj = new ClosureProjection(
            new ITypeProjection[] { classProj },
            returnProjection: null,
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "classCb");

        // Callback body should NOT contain (MyViewController) cast on the arg
        var callbacks = closureProj.CallbackDeclarations;
        var bodyCode = string.Join("\n", callbacks[0].Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.DoesNotContain("(MyViewController)arg0", bodyCode);
    }

    [Fact]
    public void ClosureWithMultipleConvertedArgs()
    {
        var closureProj = new ClosureProjection(
            new ITypeProjection[]
            {
                new StringProjection(),
                new SimpleEnumProjection("Status", "int"),
                new BlittableProjection("Int64")
            },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "multiArgCallback");

        Assert.Equal("Func<string, Status, Int64, bool>", closureProj.PublicType);

        var callbacks = closureProj.CallbackDeclarations;
        Assert.Single(callbacks);

        // Callback body should convert string and enum args back to public types
        var bodyCode = string.Join("\n", callbacks[0].Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("ToString()", bodyCode);
        Assert.Contains("(Status)", bodyCode);
    }

    #endregion

    #region ObjC Optional — nullable pointer ABI bypass

    [Fact]
    public void OptionalProjection_ObjCInner_GetReturnPlan_Direct()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("GetNSObject<UIKit.UIImage>", plan.PInvokeExpression);
        Assert.Contains("IntPtr.Zero", plan.PInvokeExpression);
        Assert.DoesNotContain("SwiftOptional", plan.PInvokeExpression);
        Assert.False(plan.RequiresUnsafe);
    }

    [Fact]
    public void OptionalProjection_ObjCInner_GetReturnPlan_IndirectResult()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("*(IntPtr*)result", plan.PInvokeExpression);
        Assert.Contains("GetNSObject<UIKit.UIImage>", plan.PInvokeExpression);
        Assert.DoesNotContain("SwiftOptional", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
    }

    [Fact]
    public void OptionalProjection_ObjCInner_GetParameterPlan()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetParameterPlan("img");

        Assert.Equal("imgBuffer", plan.PInvokeExpression);
        var line = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Handle", line.Code);
        Assert.Contains("IntPtr.Zero", line.Code);
        Assert.DoesNotContain("SwiftOptional", line.Code);
    }

    #endregion

    #region Optional × ObjC-Rooted Projection

    [Fact]
    public void OptionalProjection_ObjCRootedInner_DirectReturn_UsesNullablePointerABI()
    {
        var inner = new ObjCRootedClassProjection("CoreAnimation.CALayer");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("CoreAnimation.CALayer>", plan.PInvokeExpression);
        Assert.Contains("IntPtr.Zero", plan.PInvokeExpression);
        Assert.DoesNotContain("SwiftOptional", plan.PInvokeExpression);
    }

    [Fact]
    public void OptionalProjection_ObjCRootedInner_IndirectReturn_UsesPointerDereference()
    {
        var inner = new ObjCRootedClassProjection("CoreAnimation.CALayer");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("*(IntPtr*)result", plan.PInvokeExpression);
        Assert.Contains("CoreAnimation.CALayer>", plan.PInvokeExpression);
        Assert.DoesNotContain("SwiftOptional", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
    }

    [Fact]
    public void OptionalProjection_ObjCRootedInner_ParameterPlan_UsesHandle()
    {
        var inner = new ObjCRootedClassProjection("CoreAnimation.CALayer");
        var proj = new OptionalProjection(inner);

        var plan = proj.GetParameterPlan("layer");

        Assert.Equal("layerBuffer", plan.PInvokeExpression);
        var line = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Handle", line.Code);
        Assert.Contains("IntPtr.Zero", line.Code);
        Assert.DoesNotContain("SwiftOptional", line.Code);
    }

    [Fact]
    public void OptionalProjection_ObjCRootedInner_PublicType_IsNullable()
    {
        var inner = new ObjCRootedClassProjection("CoreAnimation.CALayer");
        var proj = new OptionalProjection(inner);

        Assert.Equal("CoreAnimation.CALayer?", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    #endregion
}
