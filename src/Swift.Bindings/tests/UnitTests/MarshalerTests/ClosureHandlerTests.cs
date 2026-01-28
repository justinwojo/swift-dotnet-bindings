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
        var result = ClosureHandler.GetCallbackFunctionName("doSomething", "callback");

        Assert.Equal("doSomething_callback_Callback", result);
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
        var result = ClosureHandler.GetCallbackFunctionName("on_complete", "handler");

        Assert.Equal("on_complete_handler_Callback", result);
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
                    Flags = TypeRecordFlags.Frozen,
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
