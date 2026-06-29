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

        // Behavior: discriminant check + proxy construction. The check reads the
        // existential's metadata pointer slot from the indirect buffer (canonical
        // None encoding for `(any P)?` is `metadata == nullptr`); SwiftOptional<T>.Case
        // is bypassed because VWT GetEnumTag is broken on Mono iOS Simulator for
        // existential optionals.
        Assert.Contains("== IntPtr.Zero", plan.PInvokeExpression);
        Assert.Contains("new ImageProcessingProxy", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
        Assert.DoesNotContain("GetEnumTag", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
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
        Assert.Equal("global::System.Func<ImagePipeline, bool>", proj.PublicType);

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

        // Per-element conversion for an opaque [any P] PARAM must mint/donate an OWNED carrier
        // (CreateOwnedExistential1) rather than aliasing the proxy's only +1 via GetOrCreate: the
        // Swift array element write is __owned (consuming) and the carrier's value-witness destroy
        // would over-release a borrowed alias (opaque sibling). The wrap-fallback is the
        // proxy ctor, retained so a suppressed-proxy site can downgrade to the no-fallback overload.
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("ExistentialContainerFactory.CreateOwnedExistential1<IDescribable>", firstLine.Code);
        Assert.Contains("new DescribableProxy(", firstLine.Code);
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
        Assert.Equal("global::System.Threading.Tasks.Task<(string, Int64)>", asyncProj.PublicType);
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

        // The some branch should contain existential container extraction. SwiftOptional has
        // owned element semantics — the `using` SwiftOptional value-witness-destroys its .Some
        // payload at teardown and the store consumes one +1 — so the existential element must
        // ride the OWNED (+1) carrier mint (CreateOwnedExistential1), not the borrowed leaf
        // (GetOrCreate) which would over-release the proxy's sole construction +1. Mirrors the
        // owned-element pairing in Array/Set/Dictionary carriers.
        var ifBlock = plan.SetupStatements.OfType<MarshalStatement.Block>()
            .First(b => b.Header.Contains("if ("));
        var someLine = Assert.IsType<MarshalStatement.Line>(ifBlock.Body[0]);
        Assert.Contains("ExistentialContainerFactory.CreateOwnedExistential1<IImageProcessing>", someLine.Code);
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

        Assert.Equal("global::System.Threading.Tasks.Task<string?>", asyncProj.PublicType);
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

        Assert.Equal("global::System.Func<string, Status, Int64, bool>", closureProj.PublicType);

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

        // Owned return: the Swift @_cdecl wrapper hands back the Some pointer at +1
        // (passRetained). The wrapper ADOPTS it via GetINativeObject(ptr, owns: true) so it
        // releases exactly once on Dispose/finalize — a bare GetNSObject (owns: false) would
        // add a second, unbalanced retain → one leaked object per call (Fix A).
        Assert.Contains("GetINativeObject<UIKit.UIImage>", plan.PInvokeExpression);
        Assert.Contains(", true)", plan.PInvokeExpression);
        Assert.DoesNotContain("GetNSObject", plan.PInvokeExpression);
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

        // Same owned-return adoption as the Direct case, reading the Some pointer through sret.
        Assert.Contains("*(IntPtr*)result", plan.PInvokeExpression);
        Assert.Contains("GetINativeObject<UIKit.UIImage>", plan.PInvokeExpression);
        Assert.Contains(", true)", plan.PInvokeExpression);
        Assert.DoesNotContain("GetNSObject", plan.PInvokeExpression);
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

    [Fact]
    public void ObjCRootedClassProjection_KeywordParamName_ProducesValidCompoundNames()
    {
        // ObjCRootedClassProjection builds _{paramName}_ptr locals.
        // With keyword param "@in", this produced "_@in_ptr" (invalid — @ mid-identifier).
        // Fix: strip verbatim prefix for compound names, keep @ for parameter references.
        var proj = new ObjCRootedClassProjection("UIKit.UIView");
        var plan = proj.GetParameterPlan("@in");

        // Compound variable name must not contain _@in_ptr (@ after prefix is invalid C#)
        Assert.DoesNotContain("_@in_ptr", plan.PInvokeExpression);
        Assert.Contains("_in_ptr", plan.PInvokeExpression);

        // Setup statements: compound name stripped, but parameter reference keeps @
        var setupCode = string.Join("\n", plan.SetupStatements.Select(s => ((MarshalStatement.Line)s).Code));
        Assert.DoesNotContain("_@in_ptr", setupCode);
        Assert.Contains("_in_ptr", setupCode);
        // Parameter reference in .Handle access must keep @ prefix
        Assert.Contains("@in.Handle", setupCode);
    }

    #endregion

    #region Bug fix regression tests

    [Fact]
    public void OptionalClass_UsesNullablePointerABI_NotSwiftOptional()
    {
        // Bug #1: Optional<SwiftClass> should use nullable pointer ABI (IntPtr.Zero for None,
        // handle for Some) instead of SwiftOptional<IntPtr> which creates Optional<Swift.Int>
        // metadata (9 bytes) triggering PayloadBuffer assert on Mono.
        var classProj = new ClassProjection("TreeNode");
        var optProj = new OptionalProjection(classProj);

        var plan = optProj.GetParameterPlan("parent");

        // Should NOT use SwiftOptional — should use direct IntPtr with null check
        Assert.Equal("parentBuffer", plan.PInvokeExpression);
        var setupCode = string.Join("\n", plan.SetupStatements
            .OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("IntPtr.Zero", setupCode);
        Assert.Contains("DangerousGetHandle()", setupCode);
        Assert.DoesNotContain("SwiftOptional", setupCode);
    }

    [Fact]
    public void OptionalBlittable_WithDangerousGetHandle_ForCdeclWrapper()
    {
        // Bug #2/#6: Optional<Int32> in @_cdecl wrappers must use DangerousGetHandle()
        // (pointer to buffer) not PayloadBuffer.Buffer (dereferenced value).
        var blittableProj = new BlittableProjection("Int32");
        var optProj = new OptionalProjection(blittableProj, useDangerousGetHandle: true);

        var plan = optProj.GetParameterPlan("count");
        var setupCode = string.Join("\n", plan.SetupStatements
            .OfType<MarshalStatement.Line>().Select(l => l.Code));

        // Should use DangerousGetHandle, NOT PayloadBuffer
        Assert.Contains("DangerousGetHandle()", setupCode);
        Assert.DoesNotContain("PayloadBuffer", setupCode);
    }

    [Fact]
    public void OptionalFrozenWithMemory_Param_UsesWrapperType_NotBufferStruct()
    {
        // A handle-backed FrozenWithMemoryProjection inner (e.g. SwiftClosedRange<T>) has no
        // nested .Buffer struct: its SwiftContainerGenericType is `{Type}.Buffer`, correct for a
        // genuine by-value ClassWithBufferStruct but nonexistent on the wrapper class. The
        // Optional<T> parameter must therefore pack as SwiftOptional<{Type}> (metadata-driven,
        // matching the return direction) and hand the public-typed wrapper value straight to
        // NewSome — never SwiftOptional<{Type}.Buffer>, which is CS0426 (no `.Buffer` member).
        var inner = new FrozenWithMemoryProjection("Swift.SwiftClosedRange<float>");
        var optProj = new OptionalProjection(inner, useDangerousGetHandle: true);

        var plan = optProj.GetParameterPlan("angularLimit");
        var allCode = FlattenSetup(plan.SetupStatements);

        Assert.Contains("SwiftOptional<Swift.SwiftClosedRange<float>>", allCode);
        Assert.DoesNotContain("Swift.SwiftClosedRange<float>.Buffer", allCode);
        // Passthrough hands the public-typed wrapper value to NewSome.
        Assert.Contains("NewSome(angularLimitValue)", allCode);
        // Large Optional → pointer to the full Optional buffer, not a PayloadBuffer<.Buffer>.
        Assert.Contains("DangerousGetHandle()", allCode);
    }

    [Fact]
    public void OptionalFrozenWithMemory_AsContainerElement_UsesWrapperType_NotBufferStruct()
    {
        // When Optional<ClosedRange<T>> is a container element (e.g. [ClosedRange<Float>?]), TWO
        // generics name the inner: the SwiftArray<...> element generic (SwiftContainerGenericType)
        // and the per-element SwiftOptional<...>.NewSome generic (GetParameterElementConversion).
        // Both must be the handle-backed wrapper, never the nonexistent `{Type}.Buffer` struct.
        var inner = new FrozenWithMemoryProjection("Swift.SwiftClosedRange<float>");
        var optProj = new OptionalProjection(inner, useDangerousGetHandle: true);

        // SwiftArray<SwiftOptional<...>> element generic.
        Assert.Equal("SwiftOptional<Swift.SwiftClosedRange<float>>", optProj.SwiftContainerGenericType);
        Assert.DoesNotContain(".Buffer", optProj.SwiftContainerGenericType);

        // Per-element NewSome conversion hands the wrapper value to a wrapper-typed SwiftOptional.
        var elemConv = optProj.GetParameterElementConversion("item");
        Assert.NotNull(elemConv);
        Assert.Contains("SwiftOptional<Swift.SwiftClosedRange<float>>.NewSome", elemConv);
        Assert.DoesNotContain(".Buffer", elemConv!);
    }

    #region Optional value-type element conversion — None must survive as a real null

    // Regression: GetReturnElementConversion for a SwiftOptional with a value-type inner once
    // emitted `{var}.ToNullable()`. Inside SwiftOptional<T>, ToNullable() returns the unconstrained
    // type parameter's `T?`, which collapses to `T` in IL for a value type — so a genuine None
    // surfaced as default(T) (0/false) and then widened back to Some(0) at the call site. This is
    // the root cause behind a `(Int32, Int32?)` tuple return reading its nil upper bound as 0.
    // The element conversion must instead branch on HasValue and build the *concrete* Nullable<T>.

    [Fact]
    public void OptionalBlittableInner_ReturnElementConversion_DoesNotUseToNullable()
    {
        // Inner public type is the lowered C# name the factory produces (Swift Int32 → "int").
        var optProj = new OptionalProjection(new BlittableProjection("int"));
        var conv = optProj.GetReturnElementConversion("opt");

        Assert.NotNull(conv);
        // Must NOT collapse a value-type None via ToNullable() (returns int, not int?).
        Assert.DoesNotContain("ToNullable", conv!);
        // Must guard on HasValue and produce a real Nullable<int> on both arms.
        Assert.Contains("opt.HasValue", conv);
        Assert.Contains("(int?)opt.Some", conv);
        Assert.Contains("default(int?)", conv);
    }

    [Fact]
    public void OptionalBoolInner_ReturnElementConversion_BuildsConcreteNullable()
    {
        // bool is the other value type that silently widens default(bool)=false to Some(false).
        var optProj = new OptionalProjection(new BlittableProjection("bool"));
        var conv = optProj.GetReturnElementConversion("flag");

        Assert.NotNull(conv);
        Assert.DoesNotContain("ToNullable", conv!);
        Assert.Contains("flag.HasValue", conv);
        Assert.Contains("default(bool?)", conv);
    }

    [Fact]
    public void OptionalValueTypeInnerWithConversion_ReturnElementConversion_DoesNotCollapseNone()
    {
        // The innerConv arm: a VALUE-type inner that DOES supply a non-null element conversion.
        // Date → DateTimeOffset (a struct) is the canonical case. `ToNullable() is { } v` would
        // always match a non-nullable value type, so a genuine None would surface as Some(default).
        // The conversion must instead guard on HasValue and yield a real default(DateTimeOffset?).
        var optProj = new OptionalProjection(new DateProjection());
        var conv = optProj.GetReturnElementConversion("stamp");

        Assert.NotNull(conv);
        // Must not route the None through the collapsing ToNullable()/is-pattern shape.
        Assert.DoesNotContain("ToNullable", conv!);
        Assert.DoesNotContain("is { }", conv);
        // Must guard on HasValue, apply the inner Date conversion to .Some, and null the None arm.
        Assert.Contains("stamp.HasValue", conv);
        Assert.Contains("stamp.Some", conv);
        Assert.Contains("AddSeconds", conv);
        Assert.Contains("default(System.DateTimeOffset?)", conv);
    }

    #endregion

    /// <summary>
    /// Renders every statement (including the bodies of if/else blocks) into one text blob so a
    /// test can assert on code emitted inside branches, not just at the top level.
    /// </summary>
    private static string FlattenSetup(IEnumerable<MarshalStatement> statements)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in statements)
        {
            switch (s)
            {
                case MarshalStatement.Line l:
                    sb.AppendLine(l.Code);
                    break;
                case MarshalStatement.Using u:
                    sb.AppendLine($"{u.Type} {u.Name} = {u.InitExpression}");
                    break;
                case MarshalStatement.Block b:
                    sb.AppendLine(b.Header);
                    sb.Append(FlattenSetup(b.Body));
                    break;
            }
        }
        return sb.ToString();
    }

    #endregion

    #region Nested IReadOnlyDictionary invariance — getter value-slot cast

    [Fact]
    public void NestedDictGetter_AppliesInvariantValueSlotCast()
    {
        // [String: [String: any P]] — the inner-dictionary VALUE projects to a concrete Dictionary<...>,
        // but the OUTER IReadOnlyDictionary value slot is invariant. The getter accessor conversion must
        // cast the value selector body to the inner dict's declared interface (its PublicType) or the
        // emitted code is CS0266. The getter formerly inlined its own AsProjected and skipped the cast
        // that BuildAsProjected owns; this asserts it now routes through the cast owner.
        var innerValue = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IBridge", "BridgeProxy");
        var innerDict = new DictionaryProjection(new StringProjection(), innerValue, isParameter: false);
        var outerDict = new DictionaryProjection(new StringProjection(), innerDict, isParameter: false);

        var (conv, _) = outerDict.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conv);
        Assert.Contains("result.AsProjected(", conv);        // routed through the shared AsProjected shape
        Assert.Contains($"({innerDict.PublicType})", conv);  // invariant value-slot cast applied
    }

    [Fact]
    public void FlatDictGetter_ScalarValue_HasNoContainerCast()
    {
        // Gating proof: [String: String] — a scalar value needs no invariant-slot cast (the cast owner
        // only wraps container values), so the targeted fix doesn't blanket-cast every dictionary getter.
        var flatDict = new DictionaryProjection(new StringProjection(), new StringProjection(), isParameter: false);

        var (conv, _) = flatDict.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conv);
        Assert.Contains("result.AsProjected(", conv);
        Assert.DoesNotContain("(IReadOnly", conv);
    }

    #endregion
}
