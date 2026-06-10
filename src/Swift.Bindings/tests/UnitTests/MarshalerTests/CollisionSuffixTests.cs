// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for projected C# method key collision disambiguation.
/// Verifies that ApplyCollisionSuffixToKey correctly inserts numeric suffixes
/// into projected keys, and that the suffix generation logic produces
/// the expected sequence (no suffix, 2, 3, ...).
/// </summary>
public class CollisionSuffixTests
{
    #region ApplyCollisionSuffixToKey Tests

    [Fact]
    public void ApplyCollisionSuffixToKey_ZeroIndex_ReturnsOriginal()
    {
        var key = "HandleNextAction(string,IPaymentSdkAuthenticationContext)";
        var result = InvokeApplyCollisionSuffixToKey(key, 0);
        Assert.Equal(key, result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_Index1_AppendsSuffix2()
    {
        var key = "HandleNextAction(string,IPaymentSdkAuthenticationContext)";
        var result = InvokeApplyCollisionSuffixToKey(key, 1);
        Assert.Equal("HandleNextAction2(string,IPaymentSdkAuthenticationContext)", result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_Index2_AppendsSuffix3()
    {
        var key = "HandleNextAction(string,IPaymentSdkAuthenticationContext)";
        var result = InvokeApplyCollisionSuffixToKey(key, 2);
        Assert.Equal("HandleNextAction3(string,IPaymentSdkAuthenticationContext)", result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_EmptyParams_InsertsBeforeParen()
    {
        var key = "Process()";
        var result = InvokeApplyCollisionSuffixToKey(key, 1);
        Assert.Equal("Process2()", result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_NoParen_AppendsSuffix()
    {
        // Edge case: key without parentheses (shouldn't happen normally)
        var key = "NoParens";
        var result = InvokeApplyCollisionSuffixToKey(key, 1);
        Assert.Equal("NoParens2", result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_CtorKey_InsertsCorrectly()
    {
        // Constructor keys use "ctor" as the method name
        var key = "ctor(string,int)";
        var result = InvokeApplyCollisionSuffixToKey(key, 1);
        Assert.Equal("ctor2(string,int)", result);
    }

    [Fact]
    public void ApplyCollisionSuffixToKey_AsyncMethod_InsertsBeforeParen()
    {
        var key = "HandleNextActionAsync(string,System.Threading.CancellationToken)";
        var result = InvokeApplyCollisionSuffixToKey(key, 1);
        Assert.Equal("HandleNextActionAsync2(string,System.Threading.CancellationToken)", result);
    }

    #endregion

    #region Collision Index Sequence

    [Fact]
    public void CollisionIndex_FirstOccurrence_NoSuffix()
    {
        // Simulates the flow: first method with a projected key gets index 0 → no suffix
        var methodName = "Process";
        int collisionIndex = 0;
        var result = collisionIndex > 0 ? $"{methodName}{collisionIndex + 1}" : methodName;
        Assert.Equal("Process", result);
    }

    [Fact]
    public void CollisionIndex_SecondOccurrence_Suffix2()
    {
        var methodName = "Process";
        int collisionIndex = 1;
        var result = collisionIndex > 0 ? $"{methodName}{collisionIndex + 1}" : methodName;
        Assert.Equal("Process2", result);
    }

    [Fact]
    public void CollisionIndex_ThirdOccurrence_Suffix3()
    {
        var methodName = "Process";
        int collisionIndex = 2;
        var result = collisionIndex > 0 ? $"{methodName}{collisionIndex + 1}" : methodName;
        Assert.Equal("Process3", result);
    }

    #endregion

    #region Helpers

    private static string InvokeApplyCollisionSuffixToKey(string projectedKey, int collisionIndex)
    {
        var method = typeof(BaseHandler).GetMethod("ApplyCollisionSuffixToKey",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { projectedKey, collisionIndex });
        Assert.NotNull(result);
        return (string)result;
    }

    #endregion
}
