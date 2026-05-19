// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="KeyPathProjection"/> — asserts the projection
/// surface (public type, P/Invoke type, parameter plan, return plan, container
/// element conversions) for all five KeyPath family arities.
///
/// BindingTests covers the end-to-end ABI round-trip (sim + device); these
/// tests fail fast if the projection's shape regresses, before the slower
/// runtime gate has to surface it.
/// </summary>
public class KeyPathProjectionTests
{
    #region Arity & PublicType — all five KeyPath shapes

    [Fact]
    public void AnyKeyPath_HasNoGenericArgs_AndUnqualifiedPublicType()
    {
        var proj = new KeyPathProjection("AnyKeyPath", []);
        Assert.Equal("AnyKeyPath", proj.ShortName);
        Assert.Empty(proj.GenericArgPublicTypes);
        Assert.Equal("Swift.AnyKeyPath", proj.PublicType);
    }

    [Fact]
    public void PartialKeyPath_HasOneGenericArg()
    {
        var proj = new KeyPathProjection("PartialKeyPath", ["MyApp.Point"]);
        Assert.Single(proj.GenericArgPublicTypes);
        Assert.Equal("Swift.PartialKeyPath<MyApp.Point>", proj.PublicType);
    }

    [Theory]
    [InlineData("KeyPath", "Swift.KeyPath<MyApp.Point, nint>")]
    [InlineData("WritableKeyPath", "Swift.WritableKeyPath<MyApp.Point, nint>")]
    [InlineData("ReferenceWritableKeyPath", "Swift.ReferenceWritableKeyPath<MyApp.Point, nint>")]
    public void TypedKeyPath_HasTwoGenericArgs_AndQualifiedPublicType(string shortName, string expected)
    {
        var proj = new KeyPathProjection(shortName, ["MyApp.Point", "nint"]);
        Assert.Equal(shortName, proj.ShortName);
        Assert.Equal(2, proj.GenericArgPublicTypes.Count);
        Assert.Equal(expected, proj.PublicType);
    }

    #endregion

    #region P/Invoke shape — single-pointer ABI

    [Fact]
    public void PInvokeType_IsIntPtr_ForAllArities()
    {
        Assert.Equal("IntPtr", new KeyPathProjection("AnyKeyPath", []).PInvokeType);
        Assert.Equal("IntPtr", new KeyPathProjection("PartialKeyPath", ["Root"]).PInvokeType);
        Assert.Equal("IntPtr", new KeyPathProjection("KeyPath", ["Root", "Value"]).PInvokeType);
    }

    [Fact]
    public void PInvokeAttribute_IsNull()
    {
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void MarshalFromSwiftType_UsesPublicType_ForFactoryDispatch()
    {
        // SwiftMarshal.MarshalFromSwiftObject<T> resolves NewFromPayload off T;
        // T must be the concrete typed wrapper, not the SafeHandle base.
        var proj = new KeyPathProjection("KeyPath", ["MyApp.Point", "nint"]);
        Assert.Equal("Swift.KeyPath<MyApp.Point, nint>", proj.MarshalFromSwiftType);
    }

    #endregion

    #region Parameter plan — DangerousGetHandle (no .Payload hop)

    [Fact]
    public void ParameterPlan_UsesDangerousGetHandle_NotPayload()
    {
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        var plan = proj.GetParameterPlan("kp");

        Assert.Equal("kp.DangerousGetHandle()", plan.PInvokeExpression);
        // SwiftKeyPathHandle IS the SafeHandle — there is no .Payload indirection
        // like SwiftClassHandle<T>. A regression that re-introduces .Payload here
        // would break compilation against the Swift.Runtime KeyPath wrappers.
        Assert.DoesNotContain(".Payload", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
        Assert.Empty(plan.CleanupStatements);
        Assert.Empty(plan.UsingDeclarations);
    }

    [Fact]
    public void ParameterPlan_DoesNotEmitAddRef_BorrowedAtGuaranteed()
    {
        // Swift @_cdecl borrows @guaranteed; the SafeHandle stays alive on the
        // managed stack for the duration of the call. A spurious DangerousAddRef
        // would over-retain.
        var proj = new KeyPathProjection("AnyKeyPath", []);
        var plan = proj.GetParameterPlan("any");
        Assert.DoesNotContain("DangerousAddRef", plan.PInvokeExpression);
        Assert.Empty(plan.CleanupStatements);
    }

    #endregion

    #region Return plan — adopts +1 retain via MarshalFromSwiftObject

    [Fact]
    public void ReturnPlan_UsesMarshalFromSwiftObject_OfPublicType()
    {
        var proj = new KeyPathProjection("WritableKeyPath", ["MyApp.Point", "nint"]);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal(
            "(Swift.WritableKeyPath<MyApp.Point, nint>)SwiftMarshal.MarshalFromSwiftObject<Swift.WritableKeyPath<MyApp.Point, nint>>(result)",
            plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void ReturnPlan_ForAnyKeyPath_UsesUnqualifiedWrapperType()
    {
        var proj = new KeyPathProjection("AnyKeyPath", []);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Contains("MarshalFromSwiftObject<Swift.AnyKeyPath>", plan.PInvokeExpression);
    }

    #endregion

    #region SwiftWrapper — foundation pass-through, no wrapper

    [Fact]
    public void RequiresSwiftWrapper_IsFalse_ForFoundationPath()
    {
        // Session 3 covers opaque pass-through only. Session 4 may flip this
        // for typed-singleton trampolines.
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        Assert.False(proj.RequiresSwiftWrapper);
        Assert.Null(proj.GetSwiftWrapperCode(new SwiftWrapperContext()));
    }

    #endregion

    #region Container element conversions — Optional<KeyPath>, [KeyPath]

    [Fact]
    public void ParameterElementConversion_UsesDangerousGetHandle()
    {
        // SwiftOptional<KeyPath<,>> / SwiftArray<KeyPath<,>> need to lower each
        // element to the same IntPtr shape as a bare parameter.
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        Assert.Equal("element.DangerousGetHandle()", proj.GetParameterElementConversion("element"));
    }

    [Fact]
    public void ReturnElementConversion_IsNull_DelegatingToWrapperConstructor()
    {
        // Return-side element marshalling is driven by MarshalFromSwift<T> on
        // the container; no per-element conversion code needed.
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        Assert.Null(proj.GetReturnElementConversion("element"));
    }

    #endregion

    #region Visitor parity

    [Fact]
    public void Accept_DispatchesToKeyPathVisitor()
    {
        var proj = new KeyPathProjection("KeyPath", ["Root", "Value"]);
        var visitor = new RecordingVisitor();
        var result = proj.Accept(visitor);

        Assert.Equal("KeyPath", result);
        Assert.Same(proj, visitor.LastKeyPath);
    }

    /// <summary>
    /// Bare-minimum recording visitor — proves that Accept() routes through the
    /// KeyPath-specific Visit(KeyPathProjection) overload (compile-time exhaustive
    /// via IProjectionVisitor&lt;T&gt;), not the generic ITypeProjection fallback.
    ///
    /// Implements every visitor branch with a no-op so the test breaks the build
    /// if a new ITypeProjection is added to IProjectionVisitor without updating
    /// the KeyPath coverage — surfaces visitor-parity regressions in this file.
    /// </summary>
    private sealed class RecordingVisitor : IProjectionVisitor<string>
    {
        public KeyPathProjection? LastKeyPath;

        public string Visit(StringProjection p) => nameof(StringProjection);
        public string Visit(BlittableProjection p) => nameof(BlittableProjection);
        public string Visit(BoolProjection p) => nameof(BoolProjection);
        public string Visit(SimpleEnumProjection p) => nameof(SimpleEnumProjection);
        public string Visit(ClassProjection p) => nameof(ClassProjection);
        public string Visit(NonFrozenStructProjection p) => nameof(NonFrozenStructProjection);
        public string Visit(FrozenWithMemoryProjection p) => nameof(FrozenWithMemoryProjection);
        public string Visit(ArrayProjection p) => nameof(ArrayProjection);
        public string Visit(DictionaryProjection p) => nameof(DictionaryProjection);
        public string Visit(SetProjection p) => nameof(SetProjection);
        public string Visit(DataProjection p) => nameof(DataProjection);
        public string Visit(OptionalProjection p) => nameof(OptionalProjection);
        public string Visit(ExistentialProjection p) => nameof(ExistentialProjection);
        public string Visit(ClosureProjection p) => nameof(ClosureProjection);
        public string Visit(AsyncProjection p) => nameof(AsyncProjection);
        public string Visit(ObjCBridgedProjection p) => nameof(ObjCBridgedProjection);
        public string Visit(ObjCBridgeableProjection p) => nameof(ObjCBridgeableProjection);
        public string Visit(ObjCRootedClassProjection p) => nameof(ObjCRootedClassProjection);
        public string Visit(NativeRemappedProjection p) => nameof(NativeRemappedProjection);
        public string Visit(TupleProjection p) => nameof(TupleProjection);
        public string Visit(DateProjection p) => nameof(DateProjection);
        public string Visit(ResultProjection p) => nameof(ResultProjection);
        public string Visit(KeyPathProjection p)
        {
            LastKeyPath = p;
            return "KeyPath";
        }
    }

    #endregion
}
