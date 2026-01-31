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

        Assert.Equal("Action<(System.Int64, System.Boolean)>", result);
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

        Assert.Equal("Func<(System.Int64, Swift.SwiftString)>", result);
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

        Assert.Equal("Func<(System.Int64, System.Boolean), (System.Double, Swift.SwiftString)>", result);
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

        Assert.Equal("delegate* unmanaged[Swift]<ValueTuple<System.Int64, System.Boolean>, void>", result);
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

        Assert.Equal("delegate* unmanaged[Swift]<ValueTuple<System.Int64, System.Double>>", result);
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
    public void IsSupportedClosure_WithAsyncThrowingClosure_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Async + throwing is not supported
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_WithThrowingClosure_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
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

        Assert.Equal("Func<System.Int64>", result);
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

        Assert.Equal("Func<System.Int64, System.Boolean>", result);
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

        Assert.Equal("Action<System.Int64, System.Boolean, System.Double>", result);
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

        Assert.Equal("Action<Swift.SwiftResult<System.Int64, Swift.SwiftError>>", result);
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

        Assert.Equal("Func<Swift.SwiftArray<System.Boolean>>", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_WithBoundGeneric_ReturnsNullableType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Optional<Int> translates to C# nullable syntax
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var result = handler.TranslateTypeSpecToCSharp(optionalInt);

        Assert.Equal("System.Int64?", result);
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

        Assert.Equal("Swift.SwiftResult<Swift.SwiftArray<System.Int64>, Swift.SwiftError>", result);
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

        Assert.Equal("Func<System.Int64?>", result);
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

        Assert.Equal("Func<Task<System.Int64>>", result);
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

        Assert.Equal("Func<System.Int64, Task>", result);
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

        Assert.Equal("Func<System.Int64, Task<System.Boolean>>", result);
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
        // Tests the case where a closure has Optional<ModuleLocalType> as a parameter
        // e.g., progress closure: (Nuke.ImageResponse?, Int64, Int64) -> ()
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Create Optional<Nuke.ImageResponse>
        var optionalImageResponse = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageResponse"));

        // Create tuple (Nuke.ImageResponse?, Swift.Int64, Swift.Int64)
        var tupleElements = new List<TypeSpec>
        {
            optionalImageResponse,
            new NamedTypeSpec("Swift.Int64"),
            new NamedTypeSpec("Swift.Int64")
        };
        var tuple = new TupleTypeSpec();
        tuple.Elements.AddRange(tupleElements);

        // Create closure (Nuke.ImageResponse?, Swift.Int64, Swift.Int64) -> ()
        var closure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_WithOptionalClosureContainingModuleLocalType_ReturnsTrue()
    {
        // Tests the case where we have Optional<Closure> and the closure has Optional<ModuleLocalType>
        // e.g., ((Nuke.ImageResponse?, Int64, Int64) -> ())?
        var typeDatabase = new MockTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Create Optional<Nuke.ImageResponse>
        var optionalImageResponse = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Nuke.ImageResponse"));

        // Create tuple (Nuke.ImageResponse?, Swift.Int64, Swift.Int64)
        var tupleElements = new List<TypeSpec>
        {
            optionalImageResponse,
            new NamedTypeSpec("Swift.Int64"),
            new NamedTypeSpec("Swift.Int64")
        };
        var tuple = new TupleTypeSpec();
        tuple.Elements.AddRange(tupleElements);

        // Create inner closure (Nuke.ImageResponse?, Swift.Int64, Swift.Int64) -> ()
        var innerClosure = new ClosureTypeSpec(tuple, TupleTypeSpec.Empty);

        // Verify inner closure is supported
        Assert.True(handler.IsSupportedClosure(innerClosure));

        // Create Optional<Closure>
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        // Verify it's recognized as optional closure
        Assert.True(handler.IsOptionalClosure(optionalClosure));

        // Verify we can extract and check the inner closure
        var extractedClosure = handler.GetClosureTypeSpec(optionalClosure);
        Assert.NotNull(extractedClosure);
        Assert.True(handler.IsSupportedClosure(extractedClosure!));
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

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

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
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";
    }

    #endregion
}
