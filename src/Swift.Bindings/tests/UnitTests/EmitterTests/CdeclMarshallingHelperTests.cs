// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for CdeclMarshallingHelper — verifies correct pointer extraction decisions
/// for @_cdecl wrapper marshalling. These tests protect against the DangerousGetHandle vs
/// PayloadBuffer confusion that caused bugs MJ-02, MJ-07, and MJ-08.
/// </summary>
public class CdeclMarshallingHelperTests
{
    #region NeedsCdeclPointerOverride

    [Fact]
    public void NeedsCdeclPointerOverride_ArrayProjection_ReturnsTrue()
    {
        var inner = new BlittableProjection("int");
        var projection = new ArrayProjection(inner, isParameter: true);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_DictionaryProjection_ReturnsTrue()
    {
        var key = new BlittableProjection("string");
        var value = new BlittableProjection("int");
        var projection = new DictionaryProjection(key, value, isParameter: true);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_SetProjection_ReturnsTrue()
    {
        var inner = new BlittableProjection("int");
        var projection = new SetProjection(inner, isParameter: true);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithArrayInner_ReturnsTrue()
    {
        var elem = new BlittableProjection("int");
        var inner = new ArrayProjection(elem, isParameter: true);
        var projection = new OptionalProjection(inner);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithDictionaryInner_ReturnsTrue()
    {
        var key = new BlittableProjection("string");
        var value = new BlittableProjection("int");
        var inner = new DictionaryProjection(key, value, isParameter: true);
        var projection = new OptionalProjection(inner);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithSetInner_ReturnsTrue()
    {
        var elem = new BlittableProjection("int");
        var inner = new SetProjection(elem, isParameter: true);
        var projection = new OptionalProjection(inner);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithBlittableInner_ReturnsTrue()
    {
        // Non-reference optional (value type) needs pointer override
        var inner = new BlittableProjection("int");
        var projection = new OptionalProjection(inner);
        Assert.True(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithClassInner_ReturnsFalse()
    {
        // Reference type optional uses nullable pointer ABI — no override
        var inner = new ClassProjection("MyClass");
        var projection = new OptionalProjection(inner);
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithObjCBridgedInner_ReturnsFalse()
    {
        var inner = new ObjCBridgedProjection("UIView");
        var projection = new OptionalProjection(inner);
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithObjCRootedInner_ReturnsFalse()
    {
        var inner = new ObjCRootedClassProjection("MyObjCClass");
        var projection = new OptionalProjection(inner);
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_OptionalWithNonFrozenStructInner_ReturnsFalse()
    {
        // OptionalProjection's NonFrozenStruct branch already emits a complete plan:
        //   IntPtr {name}Pointee = ...;
        //   IntPtr {name}Buffer = (IntPtr)(&{name}Pointee);
        // No `{name}Swift` SwiftOptional is created. Returning true here would route the
        // plan through RenderWithHandleOverride, which always appends
        //   IntPtr {name}Buffer = {name}Swift.Payload.DangerousGetHandle();
        // producing a duplicate `{name}Buffer` declaration (CS0128) and a reference to
        // an undefined `{name}Swift` (CS0103). Regression caught in Kingfisher /
        // RealityFoundation / StripePayments enum-case factories during 0.10.0.
        var inner = new NonFrozenStructProjection("MyStruct");
        var projection = new OptionalProjection(inner);
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_BlittableProjection_ReturnsFalse()
    {
        // Non-collection, non-optional doesn't need override
        var projection = new BlittableProjection("int");
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    [Fact]
    public void NeedsCdeclPointerOverride_ClassProjection_ReturnsFalse()
    {
        var projection = new ClassProjection("MyClass");
        Assert.False(CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection));
    }

    #endregion

    #region RenderWithHandleOverride

    [Fact]
    public void RenderWithHandleOverride_SkipsPayloadBufferAndEmitsDangerousGetHandle()
    {
        var plan = new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Using("SwiftArray<int>", "valueSwift", "SwiftArray<int>.FromArray(value)"),
                new MarshalStatement.Using("PayloadBuffer<IntPtr>", "valueDisposable", "valueSwift.PayloadBuffer"),
                new MarshalStatement.Line("IntPtr valueBuffer = valueDisposable.Buffer;"),
            },
            PInvokeExpression = "valueBuffer"
        };

        var output = RenderOverride(plan, "value");

        // Should keep the SwiftArray using statement
        Assert.Contains("using var valueSwift = SwiftArray<int>.FromArray(value);", output);
        // Should skip PayloadBuffer using statement
        Assert.DoesNotContain("PayloadBuffer", output);
        Assert.DoesNotContain("Disposable.Buffer", output);
        // Should emit DangerousGetHandle instead
        Assert.Contains("IntPtr valueBuffer = valueSwift.Payload.DangerousGetHandle();", output);
    }

    [Fact]
    public void RenderWithHandleOverride_PreservesNonPayloadStatements()
    {
        var plan = new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line("var tempVar = SomeSetup();"),
                new MarshalStatement.Using("SwiftDictionary<string, int>", "paramSwift", "new SwiftDictionary<string, int>()"),
                new MarshalStatement.Using("PayloadBuffer<IntPtr>", "paramDisposable", "paramSwift.PayloadBuffer"),
                new MarshalStatement.Line("IntPtr paramBuffer = paramDisposable.Buffer;"),
            },
            PInvokeExpression = "paramBuffer"
        };

        var output = RenderOverride(plan, "param");

        Assert.Contains("var tempVar = SomeSetup();", output);
        Assert.Contains("using var paramSwift = new SwiftDictionary<string, int>();", output);
        Assert.DoesNotContain("PayloadBuffer", output);
        Assert.Contains("IntPtr paramBuffer = paramSwift.Payload.DangerousGetHandle();", output);
    }

    #endregion

    #region Helpers

    private static string RenderOverride(MarshalPlan plan, string variableName)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        CdeclMarshallingHelper.RenderWithHandleOverride(writer, plan, variableName);
        writer.Flush();
        return sw.ToString();
    }

    #endregion
}
