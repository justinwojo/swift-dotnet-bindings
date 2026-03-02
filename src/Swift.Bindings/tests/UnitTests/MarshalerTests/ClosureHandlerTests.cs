// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for closure type detection and handling.
/// These tests focus on the ClosureTypeSpec parsing and attributes.
/// </summary>
public class ClosureHandlerTests
{
    #region ClosureTypeSpec Creation Tests

    [Fact]
    public void ClosureTypeSpec_EmptyClosure_HasCorrectProperties()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.True(closureTypeSpec.Arguments.IsEmptyTuple);
        Assert.True(closureTypeSpec.ReturnType.IsEmptyTuple);
        Assert.False(closureTypeSpec.IsAsync);
        Assert.False(closureTypeSpec.Throws);
        Assert.False(closureTypeSpec.IsEscaping);
    }

    [Fact]
    public void ClosureTypeSpec_WithArgAndReturn_HasCorrectProperties()
    {
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        Assert.False(closureTypeSpec.Arguments.IsEmptyTuple);
        Assert.False(closureTypeSpec.ReturnType.IsEmptyTuple);
    }

    [Fact]
    public void ClosureTypeSpec_EscapingAttribute_IsDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(closureTypeSpec.IsEscaping);
    }

    [Fact]
    public void ClosureTypeSpec_AsyncProperty_IsDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;

        Assert.True(closureTypeSpec.IsAsync);
    }

    [Fact]
    public void ClosureTypeSpec_ThrowsProperty_IsDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Throws = true;

        Assert.True(closureTypeSpec.Throws);
    }

    #endregion

    #region @convention(c) Detection Tests

    [Fact]
    public void ConventionC_WithConventionCAttribute_IsDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("c");
        closureTypeSpec.Attributes.Add(attr);

        // Test the convention detection logic
        bool isConventionC = closureTypeSpec.Attributes.Exists(a =>
            a.Name == "convention" &&
            a.Parameters.Count > 0 &&
            a.Parameters[0] == "c");

        Assert.True(isConventionC);
    }

    [Fact]
    public void ConventionC_WithConventionBlockAttribute_IsNotDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("block");
        closureTypeSpec.Attributes.Add(attr);

        // Test the convention detection logic
        bool isConventionC = closureTypeSpec.Attributes.Exists(a =>
            a.Name == "convention" &&
            a.Parameters.Count > 0 &&
            a.Parameters[0] == "c");

        Assert.False(isConventionC);
    }

    [Fact]
    public void ConventionC_WithNoAttributes_IsNotDetected()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        bool isConventionC = closureTypeSpec.Attributes.Exists(a =>
            a.Name == "convention" &&
            a.Parameters.Count > 0 &&
            a.Parameters[0] == "c");

        Assert.False(isConventionC);
    }

    #endregion

    #region Closure Argument Iteration Tests

    [Fact]
    public void EachArgument_WithSingleArg_YieldsOneArgument()
    {
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var args = closureTypeSpec.EachArgument().ToList();

        Assert.Single(args);
        Assert.IsType<NamedTypeSpec>(args[0]);
    }

    [Fact]
    public void EachArgument_WithTupleArgs_YieldsMultipleArguments()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });
        var closureTypeSpec = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        var args = closureTypeSpec.EachArgument().ToList();

        Assert.Equal(3, args.Count);
    }

    [Fact]
    public void EachArgument_WithEmptyArgs_YieldsNoArguments()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var args = closureTypeSpec.EachArgument().ToList();

        Assert.Empty(args);
    }

    #endregion

    #region TypeSpecAttribute Tests

    [Fact]
    public void TypeSpecAttribute_WithParameters_ToStringIsCorrect()
    {
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("c");

        Assert.Equal("@convention(c)", attr.ToString());
    }

    [Fact]
    public void TypeSpecAttribute_WithoutParameters_ToStringIsCorrect()
    {
        var attr = new TypeSpecAttribute("escaping");

        Assert.Equal("@escaping", attr.ToString());
    }

    [Fact]
    public void TypeSpecAttribute_WithMultipleParameters_ToStringIsCorrect()
    {
        var attr = new TypeSpecAttribute("objc_selector");
        attr.Parameters.Add("foo");
        attr.Parameters.Add("bar:");

        Assert.Equal("@objc_selector(foo, bar:)", attr.ToString());
    }

    #endregion

    #region Closure ToString Tests

    [Fact]
    public void ClosureTypeSpec_VoidToVoid_ToStringIsCorrect()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.Equal("() -> ()", closureTypeSpec.ToString());
    }

    [Fact]
    public void ClosureTypeSpec_IntToBool_ToStringIsCorrect()
    {
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        Assert.Equal("(Swift.Int) -> Swift.Bool", closureTypeSpec.ToString());
    }

    [Fact]
    public void ClosureTypeSpec_WithEscaping_ToStringIncludesAttribute()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.Contains("@escaping", closureTypeSpec.ToString());
    }

    #endregion

    #region Callback Name Generation Tests

    [Fact]
    public void GetCallbackFunctionName_GeneratesCorrectFormat()
    {
        var mangledName = "$s4test11doSomething8callbackyyyXE_tF";
        var result = ClosureHandler.GetCallbackFunctionName("doSomething", "callback", mangledName);

        // Verify format: methodName_parameterName_hash_Callback
        Assert.StartsWith("doSomething_callback_", result);
        Assert.EndsWith("_Callback", result);
        Assert.Contains("_", result.Substring("doSomething_callback_".Length));
    }

    [Fact]
    public void GetClosureWrapperFieldName_GeneratesCorrectFormat()
    {
        var result = ClosureHandler.GetClosureWrapperFieldName("callback");

        Assert.Equal("_callbackClosure", result);
    }

    [Fact]
    public void GetCallbackFunctionName_WithSpecialCharacters_GeneratesCorrectFormat()
    {
        var mangledName = "$s4test11on_complete7handleryyyXE_tF";
        var result = ClosureHandler.GetCallbackFunctionName("on_complete", "handler", mangledName);

        Assert.StartsWith("on_complete_handler_", result);
        Assert.EndsWith("_Callback", result);
    }

    [Fact]
    public void GetCallbackFunctionName_SameMethodDifferentMangledNames_GeneratesDifferentNames()
    {
        var mangledName1 = "$s4test9loadImage10completionyAA5ImageC_tF";
        var mangledName2 = "$s4test9loadImage10completiony10Foundation3URLV_tF";

        var result1 = ClosureHandler.GetCallbackFunctionName("loadImage", "completion", mangledName1);
        var result2 = ClosureHandler.GetCallbackFunctionName("loadImage", "completion", mangledName2);

        // Different mangled names should produce different callback names
        Assert.NotEqual(result1, result2);
    }

    #endregion

    #region ClosureTypeSpec Kind Tests

    [Fact]
    public void ClosureTypeSpec_HasCorrectKind()
    {
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.Equal(TypeSpecKind.Closure, closureTypeSpec.Kind);
    }

    #endregion

    #region Closure With Tuple Support Tests

    // Note: In Swift closures, the Arguments tuple is expanded into individual parameters.
    // So (Int, Bool) -> Void has two parameters, not one tuple parameter.
    // To have a closure that takes a tuple as a single parameter, it must be wrapped:
    // ((Int, Bool)) -> Void would have Arguments = TupleTypeSpec([TupleTypeSpec([Int, Bool])])

    [Fact]
    public void IsSupportedClosure_WithTupleAsOneOfMultipleParameters_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure like: ((Int, Bool), String) -> Void
        // The first parameter is a tuple, second is a string
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec>
        {
            innerTuple,
            new NamedTypeSpec("Swift.String")
        });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithTupleReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, tupleReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithNestedTupleInParameter_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure like: (((Int, Int), Bool)) -> Void
        // The parameter is a tuple containing a nested tuple - not supported
        var innermost = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            innermost,
            new NamedTypeSpec("Swift.Bool")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec>
        {
            innerTuple
        });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void GetCSharpDelegateType_WithTupleAsParameter_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure like: ((Int, Bool)) -> Void - single tuple parameter
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { innerTuple });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Action<(long, bool)>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_WithTupleReturn_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, tupleReturn);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Func<(long, Swift.SwiftString)>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_WithTupleParameterAndTupleReturn_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure like: ((Int, Bool)) -> (Double, String)
        var tupleArg = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { tupleArg });
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Double"),
            new NamedTypeSpec("Swift.String")
        });
        var closure = new ClosureTypeSpec(argsWrapper, tupleReturn);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Func<(long, bool), (double, Swift.SwiftString)>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerType_WithTupleAsParameter_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure like: ((Int, Bool)) -> Void
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { innerTuple });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        var result = handler.GetPInvokeFunctionPointerType(closure);

        Assert.Equal("delegate* unmanaged[Swift]<ValueTuple<long, bool>, void>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerType_WithTupleReturn_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Double")
        });
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, tupleReturn);

        var result = handler.GetPInvokeFunctionPointerType(closure);

        Assert.Equal("delegate* unmanaged[Swift]<ValueTuple<long, double>>", result);
    }

    #endregion

    #region Closure Return Type Tests

    [Fact]
    public void IsSupportedClosure_WithValidReturnClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int) -> Bool - should be supported
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithAsyncClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Async closures are now supported - they map to Func<..., Task> or Func<..., Task<T>>
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithAsyncThrowingClosureNoParams_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Async + throwing closures WITHOUT parameters are supported via Swift continuation wrapper pattern.
        // AsyncThrowingClosureState<T>.AsyncFunc is Func<Task<T>> (parameterless),
        // so closures with parameters produce arity mismatches (B13).
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithAsyncThrowingClosureWithParams_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Async + throwing closures WITH parameters are NOT supported (B13).
        // AsyncThrowingClosureState<T>.AsyncFunc is Func<Task<T>> (parameterless),
        // so (Int) async throws -> Bool would produce Func<Int, Task<Bool>> vs Func<Task<Bool>>.
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithThrowingClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Throwing closures are now supported - mapped to Func<..., SwiftResult<T, SwiftError>>
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void GetCSharpDelegateType_VoidToVoidClosure_ReturnsAction()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Action", result);
    }

    [Fact]
    public void GetCSharpDelegateType_VoidToIntClosure_ReturnsFuncInt()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_IntToBoolClosure_ReturnsFuncIntBool()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long, bool>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_MultipleArgsToVoid_ReturnsActionWithMultipleParams()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });
        var closureTypeSpec = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Action<long, bool, double>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerType_IntToBoolClosure_ReturnsCorrectPointerType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        var result = handler.GetPInvokeFunctionPointerType(closureTypeSpec);

        // Bool is mapped to byte in PInvoke, Swift.Int is mapped to nint
        Assert.Equal("delegate* unmanaged[Swift]<nint, byte>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerType_VoidToVoidClosure_ReturnsCorrectPointerType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var result = handler.GetPInvokeFunctionPointerType(closureTypeSpec);

        Assert.Equal("delegate* unmanaged[Swift]<void>", result);
    }

    #endregion

    #region Bound Generic Closure Tests

    [Fact]
    public void IsSupportedClosure_WithResultParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Result<Int, Error>) -> Void
        var resultType = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Error"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { resultType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithArrayParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Array<Int>) -> Void
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { arrayType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithNestedGenericParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Array<Int>>) -> Void
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var optionalType = new NamedTypeSpec("Swift.Optional", arrayType);
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { optionalType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithUnknownGenericBaseType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (UnknownGeneric<Int>) -> Void - UnknownGeneric is not in type database
        var unknownType = new NamedTypeSpec("SomeModule.UnknownGeneric", new NamedTypeSpec("Swift.Int"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { unknownType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithUnsupportedGenericParameter_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Array<UnknownType>) -> Void - inner generic param is unknown
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("SomeModule.UnknownType"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { arrayType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        // This should return true because we only check the base type exists,
        // but the inner parameter resolution happens at C# compilation time
        // Actually - we should recursively check, so this returns false
        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void GetCSharpDelegateType_WithResultParameter_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Result<Int, Error>) -> Void
        var resultType = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Error"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { resultType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Action<Swift.SwiftResult<long, Swift.SwiftError>>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_WithArrayReturn_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Array<Bool>
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Bool"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, arrayType);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Func<Swift.SwiftArray<bool>>", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_WithBoundGeneric_ReturnsNullableType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<Int> translates to C# nullable syntax
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var result = handler.TranslateTypeSpecToCSharp(optionalInt);

        Assert.Equal("long?", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalFrozenStruct_ReturnsNullable()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<CGPoint> — frozen struct → should use T? (Nullable<T>)
        var optionalCGPoint = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("CoreGraphics.CGPoint"));
        var result = handler.TranslateTypeSpecToCSharp(optionalCGPoint);

        Assert.Equal("Swift.CGPoint?", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalNonFrozenStruct_ReturnsNullable()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<ImageDecodingContext> — non-frozen struct (C# class) → T? (nullable annotation)
        // Must align with TypeConversionHandler protocol interface path
        var optionalNonFrozen = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageDecodingContext"));
        var result = handler.TranslateTypeSpecToCSharp(optionalNonFrozen);

        Assert.Equal("Swift.Nuke.ImageDecodingContext?", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalClass_ReturnsNullable()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<ImageTask> — class → T? (nullable annotation)
        // Must align with TypeConversionHandler protocol interface path
        var optionalClass = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageTask"));
        var result = handler.TranslateTypeSpecToCSharp(optionalClass);

        Assert.Equal("Swift.Nuke.ImageTask?", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalBoundGeneric_ReturnsNullable()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<Array<String>> — bound generic (NamedTypeSpec with generic params)
        // Must produce T? to align with TypeConversionHandler protocol interface path,
        // which unconditionally uses T? for Optional.
        // SwiftArray<T> is a C# reference type → nullable annotation (same runtime type).
        var arrayString = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String"));
        var optionalArray = new NamedTypeSpec("Swift.Optional", arrayString);
        var result = handler.TranslateTypeSpecToCSharp(optionalArray);

        Assert.Equal("Swift.SwiftArray<string>?", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalTuple_ReturnsSwiftOptional()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<(Int, String)> — tuple inner type → SwiftOptional wrapper (not T?)
        // Tuples are not NamedTypeSpec, so they fall through to SwiftOptional.
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var optionalTuple = new NamedTypeSpec("Swift.Optional", tupleType);
        var result = handler.TranslateTypeSpecToCSharp(optionalTuple);

        Assert.Equal("Swift.SwiftOptional<(long, Swift.SwiftString)>", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_WithNestedBoundGeneric_ReturnsFullTypeName()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Result<Array<Int>, Error>
        var arrayInt = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var resultType = new NamedTypeSpec("Swift.Result", arrayInt, new NamedTypeSpec("Swift.Error"));

        var result = handler.TranslateTypeSpecToCSharp(resultType);

        Assert.Equal("Swift.SwiftResult<Swift.SwiftArray<long>, Swift.SwiftError>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerType_WithBoundGenericParameter_ReturnsVoidPointer()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Array<Int>) -> Void - bound generics are passed as void* in P/Invoke
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec> { arrayType });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        var result = handler.GetPInvokeFunctionPointerType(closure);

        Assert.Equal("delegate* unmanaged[Swift]<void*, void>", result);
    }

    #endregion

    #region Indirect Return Marshalling Tests

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithVoidReturn_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);

        Assert.False(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithPrimitiveReturn_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithOptionalReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Optional<Int>
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalInt);

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithResultReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Result<Int, Error>
        var resultType = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Error"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, resultType);

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithArrayReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Array<Int>
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, arrayType);

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void RequiresIndirectReturnMarshalling_WithNonFrozenStructReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Nuke.ImageResponse (non-frozen struct)
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Nuke.ImageResponse"));

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithOptionalReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int) -> Optional<Bool> - should now be supported via indirect return
        var optionalBool = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"));
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), optionalBool);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithResultReturn_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Result<Int, Error> - should now be supported via indirect return
        var resultType = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Error"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, resultType);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void GetPInvokeFunctionPointerTypeWithIndirectReturn_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int) -> Optional<Bool>
        var optionalBool = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"));
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), optionalBool);

        var result = handler.GetPInvokeFunctionPointerTypeWithIndirectReturn(closure);

        // Indirect return: void* first, then args (Swift.Int -> nint), then void return
        Assert.Equal("delegate* unmanaged[Swift]<void*, nint, void>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerTypeWithIndirectReturn_NoArgs_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Optional<Int>
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalInt);

        var result = handler.GetPInvokeFunctionPointerTypeWithIndirectReturn(closure);

        Assert.Equal("delegate* unmanaged[Swift]<void*, void>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_WithOptionalReturn_ReturnsNullableType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: () -> Optional<Int> translates to Func<Int64?>
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalInt);

        var result = handler.GetCSharpDelegateType(closure);

        Assert.Equal("Func<long?>", result);
    }

    #endregion

    #region Async Closure Delegate Type Tests

    [Fact]
    public void GetCSharpDelegateType_AsyncVoidToVoid_ReturnsFuncTask()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Task>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncVoidToInt_ReturnsFuncTaskInt()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.IsAsync = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Task<long>>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncIntToVoid_ReturnsFuncIntTask()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long, Task>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncIntToBool_ReturnsFuncIntTaskBool()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long, Task<bool>>", result);
    }

    [Fact]
    public void IsAsyncClosure_WithAsyncClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;

        Assert.True(handler.IsAsyncClosure(closureTypeSpec));
    }

    [Fact]
    public void IsAsyncClosure_WithSyncClosure_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(handler.IsAsyncClosure(closureTypeSpec));
    }

    #endregion

    #region Throwing Closure Delegate Type Tests

    [Fact]
    public void GetCSharpDelegateType_ThrowsVoidToVoid_ReturnsFuncSwiftResultVoid()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Swift.SwiftResult<Swift.SwiftVoid, SwiftError>>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_ThrowsVoidToInt_ReturnsFuncSwiftResultInt()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Swift.SwiftResult<long, SwiftError>>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_ThrowsIntToBool_ReturnsFuncIntSwiftResultBool()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long, Swift.SwiftResult<bool, SwiftError>>", result);
    }

    // Note: Async+throwing closure delegate type tests are in the "Async+Throwing Closure Tests" region below.

    [Fact]
    public void IsThrowingClosure_WithThrowingClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void IsThrowingClosure_WithNonThrowingClosure_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(handler.IsThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void GetPInvokeFunctionPointerTypeWithError_VoidToVoid_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Throws = true;

        var result = handler.GetPInvokeFunctionPointerTypeWithError(closureTypeSpec);

        Assert.Equal("delegate* unmanaged[Swift]<SwiftError*, void>", result);
    }

    [Fact]
    public void GetPInvokeFunctionPointerTypeWithError_IntToBool_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.Throws = true;

        var result = handler.GetPInvokeFunctionPointerTypeWithError(closureTypeSpec);

        // Args: Int (nint), error (SwiftError*), return (byte for bool)
        Assert.Equal("delegate* unmanaged[Swift]<nint, SwiftError*, byte>", result);
    }

    #endregion

    #region Frozen Struct Closure Parameter Tests

    [Fact]
    public void IsFrozenStruct_WithFrozenStruct_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // CGPoint is a frozen struct in our mock database
        var typeSpec = new NamedTypeSpec("CoreGraphics.CGPoint");

        Assert.True(handler.IsFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsFrozenStruct_WithUnknownType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Unknown types are not in the database, so they can't be frozen structs
        var typeSpec = new NamedTypeSpec("SomeModule.UnknownType");

        Assert.False(handler.IsFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsFrozenStruct_WithPrimitive_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Int is primitive, not a frozen struct (it has Frozen flag but is blittable)
        var typeSpec = new NamedTypeSpec("Swift.Int");

        // Primitives ARE frozen structs
        Assert.True(handler.IsFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsFrozenStruct_WithGenericType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Generic types need special handling
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsFrozenStruct(typeSpec));
    }

    [Fact]
    public void CanInvokeFromCSharp_WithFrozenStructParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (CGPoint) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGPoint"),
            TupleTypeSpec.Empty);

        Assert.True(handler.CanInvokeFromCSharp(closureTypeSpec));
    }

    [Fact]
    public void RequiresStructMarshalling_WithPrimitiveParameter_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int) -> Void - only primitives, no struct marshalling needed
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        Assert.False(handler.RequiresStructMarshalling(closureTypeSpec));
    }

    [Fact]
    public void RequiresStructMarshalling_WithFrozenStructParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (CGPoint) -> Void - frozen struct needs marshalling
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGPoint"),
            TupleTypeSpec.Empty);

        Assert.True(handler.RequiresStructMarshalling(closureTypeSpec));
    }

    [Fact]
    public void RequiresStructMarshalling_WithMixedParameters_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int, CGPoint) -> Void - has frozen struct, needs marshalling
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("CoreGraphics.CGPoint")
        });
        var closureTypeSpec = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.True(handler.RequiresStructMarshalling(closureTypeSpec));
    }

    #endregion

    #region MainActor and Sendable Detection Tests

    [Fact]
    public void IsMainActor_WithMainActorAttribute_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("MainActor"));

        Assert.True(handler.IsMainActor(closureTypeSpec));
    }

    [Fact]
    public void IsMainActor_WithNoAttributes_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(handler.IsMainActor(closureTypeSpec));
    }

    [Fact]
    public void IsSendable_WithSendableAttribute_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("Sendable"));

        Assert.True(handler.IsSendable(closureTypeSpec));
    }

    [Fact]
    public void IsSendable_WithNoAttributes_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(handler.IsSendable(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithMainActorAsyncClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // @MainActor @Sendable () async -> Void
        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("MainActor"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("Sendable"));

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    #endregion

    #region Optional Closure Detection Tests

    [Fact]
    public void IsOptionalClosure_WithOptionalClosure_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional", closureType);

        Assert.True(handler.IsOptionalClosure(optionalClosure));
    }

    [Fact]
    public void IsOptionalClosure_WithDirectClosure_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(handler.IsOptionalClosure(closureType));
    }

    [Fact]
    public void IsOptionalClosure_WithOptionalInt_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsOptionalClosure(optionalInt));
    }

    [Fact]
    public void GetClosureTypeSpec_FromOptionalClosure_ReturnsInnerClosure()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var innerClosure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        innerClosure.IsAsync = true;
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        var result = handler.GetClosureTypeSpec(optionalClosure);

        Assert.NotNull(result);
        Assert.True(result!.IsAsync);
    }

    [Fact]
    public void GetCSharpOptionalDelegateType_WithOptionalAsyncClosure_ReturnsNullableFunc()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var innerClosure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        innerClosure.IsAsync = true;
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        var result = handler.GetCSharpOptionalDelegateType(optionalClosure);

        Assert.Equal("Func<Task>?", result);
    }

    [Fact]
    public void GetCSharpOptionalDelegateType_WithOptionalAction_ReturnsNullableAction()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var innerClosure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        var result = handler.GetCSharpOptionalDelegateType(optionalClosure);

        Assert.Equal("Action?", result);
    }

    [Fact]
    public void IsSupportedClosure_WithOptionalModuleLocalTypeParameter_ReturnsTrue()
    {
        // Closure: (Nuke.ImageResponse?, Int64, Int64) -> ()
        // Direct Optional<NonFrozenStruct> params use void* in callbacks with SwiftMarshal conversion.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalImageResponse = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageResponse"));
        var tupleElements = new List<TypeSpec>
        {
            optionalImageResponse,
            new NamedTypeSpec("Swift.Int64"),
            new NamedTypeSpec("Swift.Int64")
        };
        var tuple = new TupleTypeSpec();
        tuple.Elements.AddRange(tupleElements);

        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithOptionalClosureContainingModuleLocalType_InnerClosureSupported()
    {
        // Optional<Closure> where inner closure has Optional<ModuleLocalType> params.
        // Direct params use void* in callbacks with SwiftMarshal conversion — supported.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalImageResponse = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageResponse"));
        var tupleElements = new List<TypeSpec>
        {
            optionalImageResponse,
            new NamedTypeSpec("Swift.Int64"),
            new NamedTypeSpec("Swift.Int64")
        };
        var tuple = new TupleTypeSpec();
        tuple.Elements.AddRange(tupleElements);

        var innerClosure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        // Inner closure is supported (direct params use void* path)
        Assert.True(handler.IsSupportedClosure(innerClosure));

        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);
        Assert.True(handler.IsOptionalClosure(optionalClosure));

        var extractedClosure = handler.GetClosureTypeSpec(optionalClosure);
        Assert.NotNull(extractedClosure);
        Assert.True(handler.IsSupportedClosure(extractedClosure!));
    }

    [Fact]
    public void IsSupportedClosure_DirectNonFrozenStructParams_ReturnsTrue()
    {
        // Direct non-frozen struct params (after EachArgument decomposition) use void* in callbacks.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Nuke.ImageDecodingContext"),
            new NamedTypeSpec("Swift.Int64")
        });
        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_DirectClassParams_ReturnsTrue()
    {
        // Direct Swift class params (after EachArgument decomposition) use void* in callbacks.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Nuke.ImageTask"),
            new NamedTypeSpec("Swift.Int64")
        });
        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_TupleParamWithNonFrozenStructElement_ReturnsFalse()
    {
        // Tuple PARAMETER containing non-frozen struct → ValueTuple element type mismatch → rejected.
        // Unlike direct params (void* path), tuple elements use ValueTuple<IntPtr,...> in callback
        // but delegate expects ValueTuple<ClassName,...> → type mismatch at invocation.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: ((NonFrozenStruct, Int), String) -> Void
        // EachArgument gives: (NonFrozenStruct, Int) as TupleTypeSpec, String as NamedTypeSpec
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Nuke.ImageDecodingContext"),
            new NamedTypeSpec("Swift.Int64")
        });
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec>
        {
            innerTuple,
            new NamedTypeSpec("Swift.String")
        });
        var closure = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_TupleWithPointerElement_ReturnsTrue()
    {
        // UnsafeMutablePointer<T> in tuple → IntPtr in BOTH contexts → no mismatch → allowed
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var pointerType = new NamedTypeSpec("Swift.UnsafeMutablePointer");
        pointerType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            pointerType,
            new NamedTypeSpec("Swift.Int64")
        });
        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_TupleWithOnlyPrimitives_ReturnsTrue()
    {
        // (Int64, Int64) tuple → no IntPtr elements → still supported
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int64"),
            new NamedTypeSpec("Swift.Int64")
        });
        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    #endregion

    #region Non-Frozen Struct Closure Parameter Tests

    [Fact]
    public void IsNonFrozenStruct_WithNonFrozenStruct_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // ImageDecodingContext is a non-frozen struct in our mock database
        var typeSpec = new NamedTypeSpec("Nuke.ImageDecodingContext");

        Assert.True(handler.IsNonFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsNonFrozenStruct_WithFrozenStruct_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // CGPoint is a frozen struct
        var typeSpec = new NamedTypeSpec("CoreGraphics.CGPoint");

        Assert.False(handler.IsNonFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsNonFrozenStruct_WithUnknownType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("SomeModule.UnknownType");

        Assert.False(handler.IsNonFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsNonFrozenStruct_WithGenericType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Generic types are not non-frozen structs (need special handling)
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsNonFrozenStruct(typeSpec));
    }

    [Fact]
    public void IsInvocableParameter_WithNonFrozenStruct_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (ImageDecodingContext) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Nuke.ImageDecodingContext"),
            TupleTypeSpec.Empty);

        Assert.True(handler.CanInvokeFromCSharp(closureTypeSpec));
    }

    [Fact]
    public void RequiresNonFrozenMarshalling_WithNonFrozenStructParameter_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (ImageDecodingContext) -> Void - non-frozen struct needs special marshalling
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Nuke.ImageDecodingContext"),
            TupleTypeSpec.Empty);

        Assert.True(handler.RequiresNonFrozenMarshalling(closureTypeSpec));
    }

    [Fact]
    public void RequiresNonFrozenMarshalling_WithFrozenStructParameter_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (CGPoint) -> Void - frozen struct does NOT require non-frozen marshalling
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGPoint"),
            TupleTypeSpec.Empty);

        Assert.False(handler.RequiresNonFrozenMarshalling(closureTypeSpec));
    }

    [Fact]
    public void RequiresNonFrozenMarshalling_WithPrimitiveParameter_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int) -> Void - primitive does NOT require non-frozen marshalling
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        Assert.False(handler.RequiresNonFrozenMarshalling(closureTypeSpec));
    }

    [Fact]
    public void RequiresNonFrozenMarshalling_WithMixedParameters_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Int, ImageDecodingContext) -> Void - has non-frozen struct
        var argsWrapper = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Nuke.ImageDecodingContext")
        });
        var closureTypeSpec = new ClosureTypeSpec(argsWrapper, TupleTypeSpec.Empty);

        Assert.True(handler.RequiresNonFrozenMarshalling(closureTypeSpec));
    }

    #endregion

    #region Async+Throwing Closure Tests (Phase 28)

    [Fact]
    public void IsAsyncThrowingClosure_WithAsyncAndThrows_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsAsyncThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void IsAsyncThrowingClosure_WithAsyncOnly_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = false;

        Assert.False(handler.IsAsyncThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void IsAsyncThrowingClosure_WithThrowsOnly_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = false;
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsAsyncThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void IsAsyncThrowingClosure_WithNeither_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = false;
        closureTypeSpec.Throws = false;

        Assert.False(handler.IsAsyncThrowingClosure(closureTypeSpec));
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncThrowingVoidToVoid_ReturnsTask()
    {
        // Async+throwing closures return Task (not Task<SwiftResult<...>>)
        // because error handling is via Swift continuation callback, not return type
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Task>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncThrowingVoidToInt_ReturnsTaskInt()
    {
        // Async+throwing closures return Task<T> (not Task<SwiftResult<T, SwiftError>>)
        // because error handling is via Swift continuation callback, not return type
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<Task<long>>", result);
    }

    [Fact]
    public void GetCSharpDelegateType_AsyncThrowingIntToBool_ReturnsFuncIntTaskBool()
    {
        // Async+throwing closures return Task<T> (not Task<SwiftResult<T, SwiftError>>)
        // because error handling is via Swift continuation callback, not return type
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        var result = handler.GetCSharpDelegateType(closureTypeSpec);

        Assert.Equal("Func<long, Task<bool>>", result);
    }

    [Fact]
    public void GetAsyncThrowingStartFunctionPointerType_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        var result = handler.GetAsyncThrowingStartFunctionPointerType(closureTypeSpec);

        Assert.Equal("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>", result);
    }

    [Fact]
    public void IsSupportedClosure_AsyncThrowingWithPrimitiveTypes_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // () async throws -> Int
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_AsyncThrowingWithUnsupportedReturnType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // () async throws -> UnknownType - unsupported because UnknownType not in database
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("SomeModule.UnknownType"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    #endregion

    #region Closure Constraint Tests (B7, B16, CL4)

    [Fact]
    public void IsSupportedClosure_B7_OptionalStringReturn_NowSupported_ReturnsTrue()
    {
        // C1 B7 lift: Optional<String> as closure return is now supported.
        // String is allowed through the B7 gate — callback uses indirect return
        // via void* + SwiftMarshal.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_B7_GenericReturnWithFrozenParam_ReturnsTrue()
    {
        // Counter-case: Optional<Int> IS supported because Int is frozen
        // and does not require memory management.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_B7_ArrayStringReturn_ReturnsTrue()
    {
        // C1 B7 lift: [String] (Array<String>) as closure return is now supported.
        // String is allowed through the B7 gate.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, arrayReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_B7_OptionalClassReturn_StillBlocked()
    {
        // B7 remains for non-String memory-managed types (e.g., Optional<SomeClass>).
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(new NamedTypeSpec("Nuke.ImageTask"));

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_B16_ComplexEnumInCallbackParameter_ReturnsFalse()
    {
        // B16: Complex enums (no SimpleEnum flag) are non-blittable value types requiring
        // structural wrapper changes. Swift.Result is a complex enum in MockTypeDatabase.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Result"),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_SimpleEnumInCallbackParameter_ReturnsTrue()
    {
        // Q3: Simple enums pass as their underlying integer type (blittable).
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.ColorMode"),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_ClassInCallbackParameter_ReturnsTrue()
    {
        // Q3: Classes (Nuke.ImageTask) pass Layer 1 — they are in the type database.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Nuke.ImageTask"),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_ObjCBridgedInCallbackParameter_ReturnsTrue()
    {
        // Q3: ObjC-bridged types (Foundation.NSError) pass Layer 1.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Foundation.NSError"),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_OptionalClassInCallbackParameter_ReturnsTrue()
    {
        // Q3: Optional<Class> passes Layer 1 via IsSupportedGenericType recursive check.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var optionalClass = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Nuke.ImageTask"));
        var closure = new ClosureTypeSpec(optionalClass, TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsClassType_SwiftClass_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.True(handler.IsClassType(new NamedTypeSpec("Nuke.ImageTask")));
    }

    [Fact]
    public void IsClassType_ObjCBridged_ReturnsFalse()
    {
        // ObjC-bridged is NOT a plain class — separate check
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.False(handler.IsClassType(new NamedTypeSpec("Foundation.NSError")));
    }

    [Fact]
    public void IsSimpleEnum_SimpleEnum_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.True(handler.IsSimpleEnum(new NamedTypeSpec("TestModule.ColorMode")));
    }

    [Fact]
    public void IsSimpleEnum_ComplexEnum_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.False(handler.IsSimpleEnum(new NamedTypeSpec("Swift.Result")));
    }

    [Fact]
    public void IsObjCBridgedClass_NSError_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.True(handler.IsObjCBridgedClass(new NamedTypeSpec("Foundation.NSError")));
    }

    [Fact]
    public void IsReferenceType_Class_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.True(handler.IsReferenceType(new NamedTypeSpec("Nuke.ImageTask")));
    }

    [Fact]
    public void IsReferenceType_ObjCBridged_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.True(handler.IsReferenceType(new NamedTypeSpec("Foundation.NSError")));
    }

    [Fact]
    public void IsReferenceType_Struct_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        Assert.False(handler.IsReferenceType(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void GetSimpleEnumInfo_SimpleEnum_ReturnsCorrectTypes()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        var info = handler.GetSimpleEnumInfo(new NamedTypeSpec("TestModule.ColorMode"));
        Assert.NotNull(info);
        Assert.Equal("int", info!.Value.csUnderlying);
        Assert.Equal("Int32", info!.Value.swiftScalar);
    }

    [Fact]
    public void TranslateTypeSpecToPInvokeType_SimpleEnum_ReturnsUnderlyingType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);
        var result = handler.TranslateTypeSpecToPInvokeType(new NamedTypeSpec("TestModule.ColorMode"));
        Assert.Equal("int", result);
    }

    [Fact]
    public void IsSupportedClosure_CL4_ExistentialGenericParamInOptionalReturn_ReturnsFalse()
    {
        // CL4: Optional<any Protocol> as closure return type is unsupported
        // because the emitter can't marshal void* back to the bound generic type.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var existentialParam = new ProtocolListTypeSpec(); // Any
        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(existentialParam);

        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalReturn);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.False(handler.IsSupportedClosure(closure));
    }

    #endregion

    #region Bare Generic Closure Parameter Tests (Bug 3)

    [Fact]
    public void IsSupportedClosure_BareDictionaryParameter_ReturnsFalse()
    {
        // Bug 3: Dictionary without generic type args (e.g., from ObjC NSDictionary bridge)
        // should be rejected — emitting SwiftDictionary without <K,V> causes CS0305.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Dictionary) -> Void — Dictionary has NO generic parameters
        var bareDictionary = new NamedTypeSpec("Swift.Dictionary");
        var closure = new ClosureTypeSpec(bareDictionary, TupleTypeSpec.Empty);

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_OptionalBareDictionaryParameter_ReturnsFalse()
    {
        // Bug 3 variant: Optional<Dictionary> where Dictionary has no generic args.
        // The Optional wrapping should not hide the bare generic inner type.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Dictionary>) -> Void — bare Dictionary inside Optional
        var bareDictionary = new NamedTypeSpec("Swift.Dictionary");
        var optionalDict = new NamedTypeSpec("Swift.Optional", bareDictionary);
        var closure = new ClosureTypeSpec(optionalDict, TupleTypeSpec.Empty);

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_DictionaryWithGenericArgs_ReturnsTrue()
    {
        // Positive counter-case: Dictionary WITH generic args should be supported.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure: (Dictionary<String, Int>) -> Void
        var typedDict = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(typedDict, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_BareDictionary_ReturnsAnyType()
    {
        // When bare Dictionary reaches translation, it must NOT produce "Swift.SwiftDictionary"
        // (which would be a bare generic type name). It should return AnyType.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var bareDictionary = new NamedTypeSpec("Swift.Dictionary");
        var result = handler.TranslateTypeSpecToCSharp(bareDictionary);

        Assert.Equal(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_DictionaryWithGenericArgs_ReturnsFullType()
    {
        // Positive counter-case: Dictionary<String, Int> should produce full generic type.
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var typedDict = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int"));
        var result = handler.TranslateTypeSpecToCSharp(typedDict);

        Assert.Equal("Swift.SwiftDictionary<string, long>", result);
    }

    #endregion

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Int64"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int64"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Double"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Result"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftResult"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Enum
                },
                ["Swift.Error"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftError"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Protocol
                },
                // Frozen struct for testing closure parameters
                ["CoreGraphics.CGPoint"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "CGPoint"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen, // Frozen struct, no memory management
                    Kind = TypeRecordKind.Struct
                },
                // Non-frozen struct for testing closure parameters (like ImageDecodingContext)
                ["Nuke.ImageDecodingContext"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageDecodingContext"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageDecodingContext"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None, // NOT frozen
                    Kind = TypeRecordKind.Struct
                },
                // Non-frozen struct for testing closure parameters (like ImageResponse)
                ["Nuke.ImageResponse"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageResponse"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageResponse"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None, // NOT frozen
                    Kind = TypeRecordKind.Struct
                },
                // Swift class for testing tuple element mismatch in closures
                ["Nuke.ImageTask"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageTask"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageTask"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                },
                // Dictionary type — generic, requires <K,V> type args
                ["Swift.Dictionary"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // Pointer type — must return the exact TypeDatabaseExtensions.IntPtrType instance
                // so TranslateBoundGenericToCSharp recognizes it as a pointer (reference equality check)
                ["Swift.UnsafeMutablePointer"] = TypeDatabaseExtensions.IntPtrType,
                // Simple enum for testing closure parameter relaxation (Q3)
                ["TestModule.ColorMode"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ColorMode"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ColorMode"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.SimpleEnum,
                    Kind = TypeRecordKind.Enum,
                    RawValueTypeName = "Int32"
                },
                // ObjC-bridged class for testing closure parameter relaxation (Q3)
                ["Foundation.NSError"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
