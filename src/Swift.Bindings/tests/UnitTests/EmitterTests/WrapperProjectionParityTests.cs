// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies that the shared visitor classes (AccessorGetterConversionVisitor,
/// AccessorSetterConversionVisitor) produce correct output for each projection type.
/// These visitors replaced duplicated switch patterns in PropertyHandler and SubscriptHandler.
/// </summary>
public class WrapperProjectionParityTests
{
    #region Getter Visitor — Conversion Cases

    [Fact]
    public void GetterVisitor_String_ReturnsToString()
    {
        var proj = new StringProjection();
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToString()", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void GetterVisitor_Data_ReturnsToByteArray()
    {
        var proj = new DataProjection();
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToByteArray()", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void GetterVisitor_NativeRemapped_ReturnsConversionMethod()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl", requiresDisposal: true);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToNSUrl()", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void GetterVisitor_ArrayWithStringElement_ReturnsAsProjected()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, _) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("AsProjected", conversion);
    }

    [Fact]
    public void GetterVisitor_ArrayWithBlittableElement_ReturnsNull()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void GetterVisitor_DictionaryWithStringValue_ReturnsAsProjected()
    {
        var key = new BlittableProjection("Int64");
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var (conversion, _) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("AsProjected", conversion);
    }

    [Fact]
    public void GetterVisitor_SetWithStringElement_ReturnsSelectToHashSet()
    {
        var elem = new StringProjection();
        var proj = new SetProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("Select", conversion);
        Assert.Contains("ToHashSet", conversion);
        Assert.True(disposal);
    }

    #endregion

    #region Getter Visitor — Passthrough Cases

    [Theory]
    [MemberData(nameof(PassthroughProjections))]
    public void GetterVisitor_Passthrough_ReturnsNull(ITypeProjection proj)
    {
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Setter Visitor — Conversion Cases

    [Fact]
    public void SetterVisitor_String_ReturnsNewSwiftString()
    {
        var proj = new StringProjection();
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("new SwiftString(value)", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void SetterVisitor_Data_ReturnsFromByteArray()
    {
        var proj = new DataProjection();
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("Swift.Foundation.Data.FromByteArray(value)", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void SetterVisitor_NativeRemapped_WithFactory_ReturnsFactoryCall()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var (conversion, _) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("Swift.Foundation.Data.FromNSUrl(value)", conversion);
    }

    [Fact]
    public void SetterVisitor_NativeRemapped_NoFactory_ReturnsConstructor()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl");
        var (conversion, _) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("new Swift.Foundation.Data(value)", conversion);
    }

    [Fact]
    public void SetterVisitor_ArrayOfStrings_ReturnsFromEnumerable()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftArray", conversion);
        Assert.Contains("FromEnumerable", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void SetterVisitor_ArrayOfBlittable_ReturnsFromEnumerableNoConversion()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("FromEnumerable(value)", conversion);
        Assert.True(disposal);
    }

    #endregion

    #region Setter Visitor — Passthrough Cases

    [Theory]
    [MemberData(nameof(PassthroughProjections))]
    public void SetterVisitor_Passthrough_ReturnsNull(ITypeProjection proj)
    {
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Optional Getter Visitor

    [Fact]
    public void OptionalGetter_StringInner_ReturnsCastWithToString()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("ToString", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalGetter_BlittableInner_ReturnsNullableCast()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("Int64?", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalGetter_ClassInner_ReturnsIntPtrConversion()
    {
        var inner = new ClassProjection("MyClass");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        // Class optionals: accessor returns IntPtr, convert to T? via MarshalFromSwift
        Assert.NotNull(conversion);
        Assert.Contains("IntPtr.Zero", conversion);
        Assert.Contains("MarshalFromSwift", conversion);
        Assert.Contains("MyClass", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void OptionalGetter_ObjCRootedClassInner_ReturnsOwnedBridgeCall()
    {
        var inner = new ObjCRootedClassProjection("MyNSObject");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        // ObjC-rooted optionals: accessor returns IntPtr (passRetained +1),
        // convert via GetINativeObject with owns=true to avoid retain leak
        Assert.NotNull(conversion);
        Assert.Contains("IntPtr.Zero", conversion);
        Assert.Contains("GetINativeObject", conversion);
        Assert.Contains("true", conversion); // owns: true
        Assert.Contains("MyNSObject", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void OptionalGetter_ExistentialInner_ReturnsNull()
    {
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Optional Setter Visitor

    [Fact]
    public void OptionalSetter_StringInner_ReturnsSwiftOptionalWrapping()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion);
        Assert.Contains("NewSome", conversion);
        Assert.Contains("NewNone", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalSetter_BlittableInner_ReturnsSwiftOptionalWrapping()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalSetter_ClosureInner_ReturnsNull()
    {
        var inner = new ClosureProjection(Array.Empty<ITypeProjection>(), null, isEscaping: true, throws: false, isAsync: false, callbackName: "cb");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void OptionalSetter_ExistentialInner_ReturnsNull()
    {
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region ObjC bridge container reads must use owns: true

    // The Swift @_cdecl wrapper for an `async throws → [URL]?`
    // (and equivalent Set/Dictionary) result calls
    //     Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()
    // emitting a +1 retain on the bridged NSArray/NSSet/NSDictionary. The C# callback
    // must consume that +1 by reading the handle with `owns: true` — otherwise each
    // call leaks one container plus its contained NSObject elements.
    //
    // Container reads (top-level result, OptionalProjection unwrap, BuildObjCBridgeReturnPlan)
    // take `owns: true`; element reads (GetReturnElementConversion) intentionally stay
    // `owns: false` because nested NSObjects are borrowed references owned by the outer
    // collection.

    [Fact]
    public void ArrayReturn_ObjCBridge_TopLevelTakesOwnership()
    {
        // Top-level [URL] return — BuildObjCBridgeReturnPlan path.
        // Microsoft.iOS does not expose ArrayFromHandle<T>(IntPtr, bool owns); the
        // ownership-transferring overload is ArrayFromHandleFunc<T>(handle, factory,
        // releaseHandle). The third positional arg `true` releases the input handle,
        // balancing the +1 from Swift's passRetained. The factory uses GetNSObject<T>,
        // which is what the non-owning ArrayFromHandle<T>(IntPtr) does internally —
        // so per-element marshaling is unchanged.
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new ArrayProjection(elem, isParameter: false);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.NotNull(plan);
        Assert.Contains("ArrayFromHandleFunc<Foundation.NSUrl>", plan.PInvokeExpression);
        Assert.Contains("ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrl>", plan.PInvokeExpression);
        Assert.EndsWith(", true)", plan.PInvokeExpression);
    }

    [Fact]
    public void ArrayReturn_ObjCBridge_OptionalUnwrap_TakesOwnership()
    {
        // [URL]? return — OptionalProjection delegates to ArrayProjection's
        // GetReturnContainerConversion. This is the StoreKit ExternalPurchaseLink
        // .eligibleURLs path called out in the bug doc. Same ownership-transfer
        // shape as the top-level case (ArrayFromHandleFunc + releaseHandle: true),
        // because no `ArrayFromHandle<T>(IntPtr, bool owns)` overload exists.
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new ArrayProjection(elem, isParameter: false);

        var conv = proj.GetReturnContainerConversion("result");

        Assert.NotNull(conv);
        Assert.Contains("ArrayFromHandleFunc<Foundation.NSUrl>", conv);
        Assert.Contains("ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrl>", conv);
        Assert.EndsWith(", true)", conv);

        // Pre-fix shape: single-arg ArrayFromHandle (owns:false default) must not
        // regress. The fix uses the Func variant; the no-owns shape would be
        // `ArrayFromHandle<...>(result)`.
        Assert.DoesNotContain("ArrayFromHandle<Foundation.NSUrl>(result)", conv);
    }

    [Fact]
    public void ArrayReturn_ObjCBridge_ElementReads_StayBorrowed()
    {
        // Inner element NSArray reads (e.g., the inner `[URL]` inside `[[URL]]`)
        // intentionally stay non-releasing — they're borrowed references owned by
        // the outer NSArray, which already balances its own +1 via the
        // ArrayFromHandleFunc(..., releaseHandle: true) on the parent read. The
        // single-arg ArrayFromHandle<T>(IntPtr) is the correct non-owning form.
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new ArrayProjection(elem, isParameter: false);

        var conv = proj.GetReturnElementConversion("e");

        Assert.NotNull(conv);
        Assert.Contains("ArrayFromHandle<Foundation.NSUrl>(e.Handle)", conv);
        Assert.DoesNotContain("ArrayFromHandleFunc", conv);
        Assert.DoesNotContain("releaseHandle", conv);
    }

    [Fact]
    public void SetReturn_ObjCBridge_TopLevelTakesOwnership()
    {
        // Top-level Set<URL> return — BuildObjCBridgeReturnPlan path. NSSet doesn't
        // expose ArrayFromHandle, so the projection routes through
        // ObjCRuntime.Runtime.GetINativeObject<NSSet>(handle, true). The third arg
        // (`true`) is `owns:` and balances the @_cdecl wrapper's +1.
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new SetProjection(elem, isParameter: false);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.NotNull(plan);
        // Setup statement constructs the NSSet wrapper with owns:true.
        var setupTexts = string.Join("\n", plan.SetupStatements.Select(s => s.ToString()));
        Assert.Contains("GetINativeObject<Foundation.NSSet>(result, true)", setupTexts);
    }

    [Fact]
    public void SetReturn_ObjCBridge_OptionalUnwrap_TakesOwnership()
    {
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new SetProjection(elem, isParameter: false);

        var conv = proj.GetReturnContainerConversion("result");

        Assert.NotNull(conv);
        Assert.Contains("GetINativeObject<Foundation.NSSet>(result, true)", conv);
        // Pre-fix shape: GetNSObject<NSSet>(handle) without ownership transfer
        // must not regress.
        Assert.DoesNotContain("GetNSObject<Foundation.NSSet>", conv);
    }

    [Fact]
    public void DictionaryReturn_ObjCBridge_TopLevelTakesOwnership()
    {
        // Top-level [String: URL] return — BuildObjCBridgeReturnPlan path.
        var key = new StringProjection();
        var val = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new DictionaryProjection(key, val, isParameter: false);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.NotNull(plan);
        var setupTexts = string.Join("\n", plan.SetupStatements.Select(s => s.ToString()));
        Assert.Contains("GetINativeObject<Foundation.NSDictionary>(result, true)", setupTexts);
    }

    [Fact]
    public void DictionaryReturn_ObjCBridge_OptionalUnwrap_TakesOwnership()
    {
        var key = new StringProjection();
        var val = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new DictionaryProjection(key, val, isParameter: false);

        var conv = proj.GetReturnContainerConversion("result");

        Assert.NotNull(conv);
        Assert.Contains("GetINativeObject<Foundation.NSDictionary>(result, true)", conv);
        Assert.DoesNotContain("GetNSObject<Foundation.NSDictionary>", conv);
    }

    #endregion

    #region Shared Test Data

    public static TheoryData<ITypeProjection> PassthroughProjections => new()
    {
        new BlittableProjection("Int64"),
        new BoolProjection(),
        new SimpleEnumProjection("Direction", "int"),
        new ClassProjection("MyClass"),
        new NonFrozenStructProjection("MyStruct"),
        new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy"),
        new ClosureProjection(Array.Empty<ITypeProjection>(), null, isEscaping: true, throws: false, isAsync: false, callbackName: "cb"),
    };

    #endregion
}
