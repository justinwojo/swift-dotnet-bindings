// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Tests for Result&lt;T,E&gt; return value marshalling.
/// Verifies that Swift methods returning Result types are correctly
/// transported through the @_cdecl boundary via UnsafeRawPointer.
/// </summary>
public class ResultReturnTests : TestBase
{
    public ResultReturnTests(TestResults results) : base(results) { }

    public void TestResultSuccessInt()
    {
        using var test = new ResultReturnTest();
        using var result = test.GetSuccessInt();
        AssertTrue(result.IsSuccess, "getSuccessInt() should be success");
        AssertEqual(SwiftResultCase.Success, result.Case, "Case should be Success");
        AssertEqual(42, result.Success, "Success value should be 42");
    }

    public void TestResultFailureInt()
    {
        using var test = new ResultReturnTest();
        using var result = test.GetFailureInt();
        AssertTrue(result.IsFailure, "getFailureInt() should be failure");
        AssertEqual(SwiftResultCase.Failure, result.Case, "Case should be Failure");
    }

    public void TestResultSuccessPayload()
    {
        using var test = new ResultReturnTest();
        using var result = test.GetSuccessPayload();
        AssertTrue(result.IsSuccess, "getSuccessPayload() should be success");
        var payload = result.Success;
        AssertEqual("hello", payload.Value.ToString(), "Payload value should be 'hello'");
    }

    public void TestResultFailurePayload()
    {
        using var test = new ResultReturnTest();
        using var result = test.GetFailurePayload();
        AssertTrue(result.IsFailure, "getFailurePayload() should be failure");
    }

    public void TestResultStaticMethod()
    {
        using var result = ResultReturnTest.GetStaticSuccess();
        AssertTrue(result.IsSuccess, "staticSuccess() should be success");
        AssertEqual(99, result.Success, "Static success value should be 99");
    }

    public void TestResultProperty()
    {
        using var test = new ResultReturnTest();
        using var result = test.CurrentResult;
        AssertTrue(result.IsSuccess, "currentResult should be success");
        AssertEqual(7, result.Success, "Property result value should be 7");
    }
}
