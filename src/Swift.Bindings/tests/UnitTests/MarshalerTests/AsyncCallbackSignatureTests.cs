// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that verify Swift wrapper callback signatures match C# CallbackDeclaration
/// signatures for async methods. Catches ABI-level drift between the two sides
/// of the async callback bridge.
///
/// The async bridge works as follows:
/// - Swift side: @convention(c) (ReturnType, Int64) -> Void callback
/// - C# side: [UnmanagedCallersOnly] static void Callback(PInvokeType rawResult, IntPtr task)
/// These must be ABI-compatible or the callback will corrupt the stack.
/// </summary>
public class AsyncCallbackSignatureTests
{
    private static readonly SwiftWrapperContext DefaultContext = new()
    {
        MangledName = "$sTest",
        ModuleName = "Test",
        MethodName = "testMethod",
        OriginalCallExpression = "testMethod()"
    };

    #region Success Callback — Void Return

    [Fact]
    public void SuccessCallback_VoidReturn_HasOnlyTaskParam()
    {
        var proj = new AsyncProjection(null, throws: false, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractSuccessCallbackSignatures(proj);

        Assert.Single(swiftParams);
        Assert.Single(csharpTypes);
        // Task param: C# IntPtr ↔ Swift Int64 (both 8 bytes on 64-bit iOS)
        Assert.Equal("IntPtr", csharpTypes[0]);
        Assert.Equal("Int64", swiftParams[0]);
    }

    [Fact]
    public void SuccessCallback_VoidReturn_SwiftWrapperCallsCallbackWithTaskOnly()
    {
        var proj = new AsyncProjection(null, throws: false, callbackPrefix: "test");
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;

        // Should call callback(task), not callback(result, task)
        Assert.Contains("callback(task)", swiftCode);
    }

    #endregion

    #region Success Callback — Return Type Reconciliation

    [Theory]
    [MemberData(nameof(SuccessCallbackTestCases))]
    public void SuccessCallback_ParamCountAndTypesMatch(
        string description, ITypeProjection innerProjection, string expectedSwiftReturnType)
    {
        var proj = new AsyncProjection(innerProjection, throws: false, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractSuccessCallbackSignatures(proj);

        // Should have exactly 2 params: return value + task
        Assert.True(2 == swiftParams.Count, $"{description}: expected 2 Swift params, got {swiftParams.Count}");
        Assert.True(2 == csharpTypes.Count, $"{description}: expected 2 C# params, got {csharpTypes.Count}");

        // Return param: C# PInvokeType should map to expected Swift type
        Assert.True(innerProjection.PInvokeType == csharpTypes[0],
            $"{description}: C# return param should be '{innerProjection.PInvokeType}', got '{csharpTypes[0]}'");
        Assert.True(expectedSwiftReturnType == swiftParams[0],
            $"{description}: Swift return param should be '{expectedSwiftReturnType}', got '{swiftParams[0]}'");

        // Task param: always IntPtr ↔ Int64
        Assert.Equal("IntPtr", csharpTypes[1]);
        Assert.Equal("Int64", swiftParams[1]);
    }

    public static IEnumerable<object[]> SuccessCallbackTestCases()
    {
        // Blittable integer types
        yield return new object[] { "Int64", new BlittableProjection("Int64"), "Int64" };
        yield return new object[] { "Int32", new BlittableProjection("Int32"), "Int32" };
        yield return new object[] { "UInt32", new BlittableProjection("UInt32"), "UInt32" };
        yield return new object[] { "UInt64", new BlittableProjection("UInt64"), "UInt64" };
        yield return new object[] { "byte", new BlittableProjection("byte"), "UInt8" };

        // Blittable floating-point types
        yield return new object[] { "double", new BlittableProjection("double"), "Double" };
        yield return new object[] { "Float", new BlittableProjection("Float"), "Float" };

        // String — SwiftString passes as UnsafeRawPointer in callbacks
        yield return new object[] { "String", new StringProjection(), "UnsafeRawPointer" };

        // Pointer-based types — IntPtr maps to UnsafeRawPointer
        yield return new object[] { "ObjCBridged", new ObjCBridgedProjection("UIImage"), "UnsafeRawPointer" };
        yield return new object[] { "NonFrozenStruct", new NonFrozenStructProjection("Pipeline"), "UnsafeRawPointer" };
        yield return new object[] { "Class", new ClassProjection("MyObj"), "UnsafeRawPointer" };
        yield return new object[] { "Array", new ArrayProjection(new BlittableProjection("Int64"), false), "UnsafeRawPointer" };
        yield return new object[] { "Dictionary", new DictionaryProjection(new BlittableProjection("Int64"), new BlittableProjection("Int64"), false), "UnsafeRawPointer" };
        yield return new object[] { "Optional", new OptionalProjection(new BlittableProjection("Int64")), "UnsafeRawPointer" };

        // Enum — underlying type maps through
        yield return new object[] { "SimpleEnum(int)", new SimpleEnumProjection("Direction", "int"), "Int32" };
        yield return new object[] { "SimpleEnum(Int64)", new SimpleEnumProjection("Size", "Int64"), "Int64" };

        // nint/nuint (native-sized integers)
        yield return new object[] { "nint", new BlittableProjection("nint"), "Int" };
        yield return new object[] { "nuint", new BlittableProjection("nuint"), "UInt" };
    }

    #endregion

    #region Error Callback Reconciliation

    [Fact]
    public void ErrorCallback_HasFiveParams()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        Assert.Equal(5, swiftParams.Count);
        Assert.Equal(5, csharpTypes.Count);
    }

    [Fact]
    public void ErrorCallback_ErrorPtrParam_IsAbiCompatible()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        // errorPtr: IntPtr ↔ UnsafeRawPointer (both pointer-sized)
        Assert.Equal("IntPtr", csharpTypes[0]);
        AssertAbiCompatible(csharpTypes[0], swiftParams[0], "errorPtr");
    }

    [Fact]
    public void ErrorCallback_ErrorSizeParam_IsAbiCompatible()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        // errorSize: nint ↔ Int (both pointer-sized signed integer)
        Assert.Equal("nint", csharpTypes[1]);
        AssertAbiCompatible(csharpTypes[1], swiftParams[1], "errorSize");
    }

    [Fact]
    public void ErrorCallback_MsgParam_IsAbiCompatible()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        // msg: IntPtr ↔ UnsafePointer<CChar> (both pointer-sized)
        Assert.Equal("IntPtr", csharpTypes[2]);
        AssertAbiCompatible(csharpTypes[2], swiftParams[2], "msg");
    }

    [Fact]
    public void ErrorCallback_IsCancelledParam_IsAbiCompatible()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        // isCancelled: int ↔ Int32
        Assert.Equal("int", csharpTypes[3]);
        AssertAbiCompatible(csharpTypes[3], swiftParams[3], "isCancelled");
    }

    [Fact]
    public void ErrorCallback_TaskParam_IsAbiCompatible()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var (swiftParams, csharpTypes) = ExtractErrorCallbackSignatures(proj);

        // task: IntPtr ↔ Int64 (both 8 bytes on 64-bit)
        Assert.Equal("IntPtr", csharpTypes[4]);
        AssertAbiCompatible(csharpTypes[4], swiftParams[4], "task");
    }

    [Fact]
    public void ErrorCallback_SignatureIsIndependentOfReturnType()
    {
        // Error callback signature should be the same regardless of the inner return type
        var proj1 = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var proj2 = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var proj3 = new AsyncProjection(null, throws: true, callbackPrefix: "test");

        var err1 = proj1.CallbackDeclarations.First(c => c.MethodName.Contains("Error"));
        var err2 = proj2.CallbackDeclarations.First(c => c.MethodName.Contains("Error"));
        var err3 = proj3.CallbackDeclarations.First(c => c.MethodName.Contains("Error"));

        Assert.Equal(err1.Signature, err2.Signature);
        Assert.Equal(err2.Signature, err3.Signature);
    }

    #endregion

    #region SwiftCallbackReturnType Override

    [Fact]
    public void SwiftCallbackReturnType_Override_AppearsInSwiftWrapper()
    {
        // When emitter provides SwiftCallbackReturnType, it should appear in the Swift
        // callback signature instead of the MapPInvokeTypeToSwift result
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var context = DefaultContext with { SwiftCallbackReturnType = "(String, Int)" };
        var swiftCode = proj.GetSwiftWrapperCode(context)!;

        // The override tuple type should appear in the callback signature
        Assert.Contains("(String, Int), Int64) -> Void", swiftCode);
    }

    [Fact]
    public void SwiftCallbackReturnType_Override_CSharpSideUsesProjectionPInvokeType()
    {
        // C# CallbackDeclaration always uses the projection's PInvokeType,
        // independent of SwiftCallbackReturnType (which only affects Swift wrapper)
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var callback = proj.CallbackDeclarations[0];
        var csharpTypes = ExtractCSharpParamTypes(callback.Signature);

        // Should be SwiftString (from StringProjection.PInvokeType), not affected by override
        Assert.Equal("SwiftString", csharpTypes[0]);
    }

    [Fact]
    public void SwiftCallbackReturnType_NotSet_FallsBackToMapPInvokeTypeToSwift()
    {
        // Without override, MapPInvokeTypeToSwift converts the C# PInvokeType
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;

        // StringProjection.PInvokeType is "SwiftString" → MapPInvokeTypeToSwift → "UnsafeRawPointer"
        Assert.Contains("UnsafeRawPointer, Int64) -> Void", swiftCode);
    }

    #endregion

    #region Throwing vs Non-Throwing Structural Checks

    [Fact]
    public void NonThrowing_SwiftWrapper_HasNoErrorCallbackInSignature()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;

        Assert.DoesNotContain("errorCallback", swiftCode);
        Assert.DoesNotContain("do {", swiftCode);
        Assert.DoesNotContain("} catch", swiftCode);
    }

    [Fact]
    public void Throwing_SwiftWrapper_HasErrorCallbackInSignature()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;

        Assert.Contains("_ errorCallback: @convention(c)", swiftCode);
        Assert.Contains("do {", swiftCode);
        Assert.Contains("} catch {", swiftCode);
    }

    [Fact]
    public void Throwing_SuccessCallback_SignatureMatchesNonThrowing()
    {
        // The success callback signature should be identical regardless of throws
        var throwingProj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var nonThrowingProj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");

        var throwingSuccess = throwingProj.CallbackDeclarations[0];
        var nonThrowingSuccess = nonThrowingProj.CallbackDeclarations[0];

        Assert.Equal(throwingSuccess.Signature, nonThrowingSuccess.Signature);
    }

    #endregion

    #region MapPInvokeTypeToSwift Coverage

    [Fact]
    public void AllPInvokeTypesInSuccessCallbackTestCases_AreHandled()
    {
        // Verify that every PInvokeType used in our test cases is in the ABI compatibility map.
        // If this test fails, a new projection type was added to the test cases without
        // updating the ABI map — could indicate a gap in MapPInvokeTypeToSwift.
        foreach (var testCase in SuccessCallbackTestCases())
        {
            var innerProjection = (ITypeProjection)testCase[1];
            var pInvokeType = innerProjection.PInvokeType;
            Assert.True(
                AbiCompatibleSwiftTypes.ContainsKey(pInvokeType),
                $"PInvokeType '{pInvokeType}' from {testCase[0]} is not in ABI compatibility map");
        }
    }

    #endregion

    #region Helpers

    private (List<string> swiftParams, List<string> csharpTypes) ExtractSuccessCallbackSignatures(
        AsyncProjection proj)
    {
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;
        var callback = proj.CallbackDeclarations[0];
        return (ExtractSwiftSuccessCallbackParams(swiftCode), ExtractCSharpParamTypes(callback.Signature));
    }

    private (List<string> swiftParams, List<string> csharpTypes) ExtractErrorCallbackSignatures(
        AsyncProjection proj)
    {
        var swiftCode = proj.GetSwiftWrapperCode(DefaultContext)!;
        var errorCallback = proj.CallbackDeclarations.First(c => c.MethodName.Contains("Error"));
        return (ExtractSwiftErrorCallbackParams(swiftCode), ExtractCSharpParamTypes(errorCallback.Signature));
    }

    /// <summary>
    /// Extracts parameter types from the success callback's @convention(c) signature in Swift wrapper code.
    /// e.g., "_ callback: @convention(c) (Int64, Int64) -> Void" → ["Int64", "Int64"]
    /// </summary>
    private static List<string> ExtractSwiftSuccessCallbackParams(string swiftCode)
    {
        var match = Regex.Match(swiftCode, @"_ callback: @convention\(c\) \(([^)]+)\) -> Void");
        Assert.True(match.Success, "Could not find success callback @convention(c) signature in Swift wrapper code");
        return match.Groups[1].Value.Split(',').Select(s => s.Trim()).ToList();
    }

    /// <summary>
    /// Extracts parameter types from the error callback's @convention(c) signature in Swift wrapper code.
    /// Handles UnsafePointer&lt;CChar&gt; (angle brackets don't interfere with the regex).
    /// </summary>
    private static List<string> ExtractSwiftErrorCallbackParams(string swiftCode)
    {
        var match = Regex.Match(swiftCode, @"_ errorCallback: @convention\(c\) \(([^)]+)\) -> Void");
        Assert.True(match.Success, "Could not find error callback @convention(c) signature in Swift wrapper code");
        // Split on ", " to avoid splitting inside angle brackets like UnsafePointer<CChar>
        return match.Groups[1].Value.Split(", ").Select(s => s.Trim()).ToList();
    }

    /// <summary>
    /// Extracts C# parameter types from a CallbackDeclaration.Signature string.
    /// e.g., "Int64 rawResult, IntPtr task" → ["Int64", "IntPtr"]
    /// </summary>
    private static List<string> ExtractCSharpParamTypes(string signature)
    {
        return signature.Split(',')
            .Select(p => p.Trim().Split(' ')[0])
            .ToList();
    }

    /// <summary>
    /// ABI-compatible type pairs. Multiple Swift types may correspond to a single C# type
    /// because they're the same size/representation at the ABI level.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> AbiCompatibleSwiftTypes = new()
    {
        ["IntPtr"] = new() { "UnsafeRawPointer", "UnsafePointer<CChar>", "Int64" },
        ["nint"] = new() { "Int" },
        ["nuint"] = new() { "UInt" },
        ["int"] = new() { "Int32" },
        ["Int32"] = new() { "Int32" },
        ["Int64"] = new() { "Int64" },
        ["UInt32"] = new() { "UInt32" },
        ["UInt64"] = new() { "UInt64" },
        ["Double"] = new() { "Double" },
        ["double"] = new() { "Double" },
        ["Float"] = new() { "Float" },
        ["float"] = new() { "Float" },
        ["byte"] = new() { "UInt8" },
        ["SwiftString"] = new() { "UnsafeRawPointer" },
    };

    private static void AssertAbiCompatible(string csharpType, string swiftType, string paramDescription)
    {
        if (AbiCompatibleSwiftTypes.TryGetValue(csharpType, out var compatibleTypes))
        {
            Assert.True(compatibleTypes.Contains(swiftType),
                $"ABI mismatch for {paramDescription}: C# '{csharpType}' is not compatible " +
                $"with Swift '{swiftType}'. Expected one of: {string.Join(", ", compatibleTypes)}");
        }
        else
        {
            Assert.Fail(
                $"Unknown C# type '{csharpType}' for {paramDescription} — " +
                $"add it to AbiCompatibleSwiftTypes dictionary");
        }
    }

    #endregion
}
