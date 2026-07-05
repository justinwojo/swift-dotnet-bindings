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

    #region FB-1b — failable-init overload-collapse factory naming

    // Two `init?` overloads whose parameter labels differ (messengerPageId vs nonce) but erase to the
    // same projected C# `TryCreate(IEnumerable<string>, LoginTracking, string, out …)` signature. Before
    // FB-1b the second was dropped as DuplicateSignature; now the first-declared keeps the plain
    // `TryCreate` and the colliding sibling recovers under a label-disambiguated static-factory name.
    // Behavior (not exact strings): the distinguishing label appears in the sibling's name, the SHARED
    // labels do not, and the recovered name never collapses onto the winner's plain `TryCreate`.

    [Fact]
    public void BuildFailableFactoryName_CollidingSibling_SuffixesOnlyTheDistinguishingLabel()
    {
        var winner = FailableInit(("permissions", "Swift.Array"), ("tracking", "TestModule.LoginTracking"), ("nonce", "Swift.String"));
        var sibling = FailableInit(("permissions", "Swift.Array"), ("tracking", "TestModule.LoginTracking"), ("messengerPageId", "Swift.String"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.Contains("MessengerPageId", name);   // the label that distinguishes this overload
        Assert.DoesNotContain("Permissions", name);  // shared with the winner → not a distinguisher
        Assert.DoesNotContain("Tracking", name);
        AssertValidIdentifier(name);
    }

    [Fact]
    public void BuildFailableFactoryName_NoWinner_AllLabelsDistinguish()
    {
        // When the plain slot was claimed by a non-failable constructor (winner == null), every usable
        // label distinguishes the recovered factory.
        var sibling = FailableInit(("host", "Swift.String"), ("port", "Swift.Int"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner: null, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.Contains("Host", name);
        Assert.Contains("Port", name);
        AssertValidIdentifier(name);
    }

    [Fact]
    public void BuildFailableFactoryName_NoUsableLabels_FallsBackToUniqueNumericName()
    {
        // Pathological all-synthesized-label case: no distinguishing label to suffix, so a numeric
        // fallback keeps the name unique and off the winner's plain `TryCreate`.
        var sibling = FailableInit(("arg0", "Swift.String"), ("arg1", "Swift.Int"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner: null, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.StartsWith("TryCreate", name);
        AssertValidIdentifier(name);
    }

    private static void AssertValidIdentifier(string name)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.True(char.IsLetter(name[0]) || name[0] == '_');
        Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c == '_'));
    }

    private static MethodDecl FailableInit(params (string label, string type)[] labels)
    {
        var sig = new List<ArgumentDecl> { InitArg(string.Empty, "Swift.Optional") }; // CSSignature[0] = return
        foreach (var (label, type) in labels)
            sig.Add(InitArg(label, type));
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4initX",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = true,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
    }

    private static ArgumentDecl InitArg(string label, string type) => new ArgumentDecl
    {
        Name = label,
        PrivateName = label,
        SwiftTypeSpec = new NamedTypeSpec(type),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

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
