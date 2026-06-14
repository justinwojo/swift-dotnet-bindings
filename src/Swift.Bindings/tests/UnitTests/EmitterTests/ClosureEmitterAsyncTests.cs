// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ClosureEmitter.Async — async closure emission
/// (EmitAsyncThrowingClosureCallback, EmitAsyncThrowingClosureCallbackPointer,
/// EmitAsyncThrowingClosureMarshallingSetup).
/// </summary>
public class ClosureEmitterAsyncTests
{
    #region EmitAsyncThrowingClosureCallback

    [Fact]
    public void AsyncCallback_VoidReturn_EmitsUnmanagedCallersOnly()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF");

        var result = output.ToString();
        Assert.Contains("[UnmanagedCallersOnly(CallConvs", result);
        Assert.Contains("CallConvCdecl", result);
        Assert.Contains("_Start", result);
        Assert.Contains("AsyncThrowingClosureStateVoid", result);
        Assert.Contains("RunVoidAsync", result);
    }

    [Fact]
    public void AsyncCallback_GenericReturn_EmitsRunAsync()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure returning a class type
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("TestModule.Loader"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "loadData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule8loadDatayyF");

        var result = output.ToString();
        Assert.Contains("[UnmanagedCallersOnly", result);
        Assert.Contains("_Start", result);
        Assert.Contains("AsyncThrowingClosureState<", result);
        Assert.Contains("RunAsync", result);
        Assert.DoesNotContain("RunVoidAsync", result);
        Assert.DoesNotContain("RunDataAsync", result);
    }

    [Fact]
    public void AsyncCallback_DataReturn_EmitsRunDataAsync()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure returning Foundation.Data
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Foundation.Data"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "fetchData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule9fetchDatayyF");

        var result = output.ToString();
        Assert.Contains("RunDataAsync", result);
        Assert.Contains("nint", result); // Data callback takes (box, dataPtr, len)
    }

    [Fact]
    public void AsyncCallback_ContextPtr_IsFirstParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s_mangled");

        var result = output.ToString();
        Assert.Contains("IntPtr contextPtr", result);
        Assert.Contains("IntPtr continuationBoxPtr", result);
        Assert.Contains("IntPtr successFuncPtr", result);
        Assert.Contains("IntPtr errorFuncPtr", result);
    }

    [Fact]
    public void AsyncCallback_DataReturn_StateUsesSwiftDataNotByteArray()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure returning Foundation.Data — projected as byte[] publicly,
        // but AsyncThrowingClosureState<T> must use Swift.Foundation.Data (ABI type)
        // because AsyncClosureHelper.RunDataAsync expects it.
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Foundation.Data"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "fetchData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule9fetchDatayyF");

        var result = output.ToString();
        // State type must use ABI type (Swift.Foundation.Data), not projected type (byte[])
        Assert.Contains("AsyncThrowingClosureState<Swift.Foundation.Data>", result);
        Assert.DoesNotContain("AsyncThrowingClosureState<byte[]>", result);
    }

    // ---- Finding 37: mechanical resume-once -----------------------------------------------------

    [Fact]
    public void AsyncCallback_ThrowingVoid_ResumesExactlyOnceViaGuard()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF");

        var result = output.ToString();

        // A single shared resume guard, constructed once and claimed by every resume delegate so a
        // success and an error can never both consume the continuation box.
        Assert.Contains("new global::Swift.Runtime.AsyncResumeGuard()", result);
        Assert.Contains("if (!__resumeGuard.TryClaim()) return;", result);

        // The Start-thunk body is wrapped in a guarded envelope (ResumeBoxError policy): a
        // marshalling fault resumes the box with the error rather than escaping into native.
        Assert.Contains("catch (global::System.Exception __uco_ex)", result);
        Assert.Contains("AsyncClosureHelper.ReportError(__uco_ex, continuationBoxPtr, errorAction)", result);

        // The context-type mismatch resumes the box with an error rather than returning silently
        // and leaving the Swift task awaiting forever.
        Assert.Contains("ReportError(new global::System.InvalidOperationException(", result);
    }

    [Fact]
    public void AsyncCallback_ThrowingGenericReturn_UsesResumeBoxErrorPolicy()
    {
        var typeDatabase = CreateTypeDatabaseWithLoader();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("TestModule.Loader"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "loadData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule8loadDatayyF");

        var result = output.ToString();
        Assert.Contains("if (!__resumeGuard.TryClaim()) return;", result);
        Assert.Contains("AsyncClosureHelper.ReportError(__uco_ex, continuationBoxPtr, errorAction)", result);
        Assert.Contains("ReportError(new global::System.InvalidOperationException(", result);
        // Throwing closures resume with an error; they never FailFast on the sync failure paths.
        Assert.DoesNotContain("FailFastNonThrowing", result);
    }

    [Fact]
    public void AsyncCallback_NonThrowingReturn_UsesFailFastPolicy()
    {
        var typeDatabase = CreateTypeDatabaseWithLoader();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Non-throwing async closure: there is no Swift error channel to resume with, so the
        // synchronous failure paths must FailFast loudly rather than resume-with-error.
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("TestModule.Loader"))
        {
            IsAsync = true,
            Throws = false
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "loadData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule8loadDatayyF");

        var result = output.ToString();
        Assert.Contains("RunAsyncNonThrowing", result);
        // Still resume-once guarded on the success delegate.
        Assert.Contains("if (!__resumeGuard.TryClaim()) return;", result);
        // FailFast on both synchronous failure paths; never resume-with-error (no error channel).
        Assert.Contains("catch (global::System.Exception __uco_ex)", result);
        Assert.Contains("FailFastNonThrowing(__uco_ex)", result);
        Assert.Contains("FailFastNonThrowing(new global::System.InvalidOperationException(", result);
        Assert.DoesNotContain("ReportError", result);
    }

    [Fact]
    public void AsyncCallback_ContextHandleResolution_IsInsideGuardedTry()
    {
        // Finding 37 follow-up: GCHandle.FromIntPtr throws on a zero/corrupt contextPtr. If it is
        // resolved before the guarded try, that exception escapes the [UnmanagedCallersOnly]
        // boundary with the box never resumed. It must be resolved INSIDE the try so the catch
        // resumes-with-error / FailFasts instead.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallback(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF");

        var result = output.ToString();

        var guardIdx = result.IndexOf("new global::Swift.Runtime.AsyncResumeGuard()", System.StringComparison.Ordinal);
        var tryIdx = result.IndexOf("try", System.StringComparison.Ordinal);
        var handleIdx = result.IndexOf("GCHandle.FromIntPtr(contextPtr)", System.StringComparison.Ordinal);

        Assert.True(guardIdx >= 0 && tryIdx >= 0 && handleIdx >= 0);
        // The resume guard is constructed before the try (the catch resumes through its delegates);
        // the context handle is resolved after the try opens, so a bad contextPtr is caught.
        Assert.True(guardIdx < tryIdx, "resume guard must be constructed before the try");
        Assert.True(tryIdx < handleIdx, "GCHandle.FromIntPtr(contextPtr) must be inside the guarded try");
    }

    [Fact]
    public void AsyncCallback_NonThrowingStringReturn_FailsClosed()
    {
        // Non-throwing async String/Data returns are gated out upstream
        // (IsBaselineAsyncNonThrowingClosure requires a blittable-primitive return). If that gate
        // is ever widened, the emitter must refuse rather than emit a state-type mismatch — the
        // String/Data continuation helpers bind AsyncThrowingClosureState, not the non-throwing
        // AsyncClosureState this path would build.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Swift.String"))
        {
            IsAsync = true,
            Throws = false
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        var ex = Assert.Throws<System.NotSupportedException>(() =>
            ClosureEmitter.EmitAsyncThrowingClosureCallback(
                csWriter, "loadName", "handler", closureTypeSpec, closureHandler,
                "$s10TestModule8loadNameyyF"));
        Assert.Contains("Non-throwing async closures with String or Data returns", ex.Message);
    }

    [Fact]
    public void AsyncSetup_DataReturn_StateUsesSwiftDataWithConversion()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Foundation.Data"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
            csWriter, "fetchData", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule9fetchDatayyF");

        var result = output.ToString();
        // State type must use Swift.Foundation.Data, not byte[]
        Assert.Contains("AsyncThrowingClosureState<Swift.Foundation.Data>", result);
        Assert.DoesNotContain("AsyncThrowingClosureState<byte[]>", result);
        // Must convert Func<Task<byte[]>> to Func<Task<Swift.Foundation.Data>>
        Assert.Contains("Swift.Foundation.Data.FromByteArray(r)", result);
    }

    #endregion

    #region EmitAsyncThrowingClosureCallbackPointer

    [Fact]
    public void AsyncCallbackPointer_EmitsStaticField()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler, "$s_mangled");

        var result = output.ToString();
        Assert.Contains("private static unsafe readonly", result);
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>", result);
        Assert.Contains("_Start", result);
    }

    [Fact]
    public void AsyncCallbackPointer_NameDerivedFromMethodAndParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(
            csWriter, "fetchResource", "completion", closureTypeSpec, closureHandler, "$s_xyz");

        var result = output.ToString();
        // The callback name is derived from method name + parameter name
        Assert.Contains("fetchResource", result);
        Assert.Contains("completion", result);
    }

    [Fact]
    public void AsyncCallbackPointer_WithPrimitiveArg_EmitsWiderFuncPtr()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // (Swift.Int32) async throws -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int32"),
            TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler, "$s_m");

        var result = output.ToString();
        // 1-arg: (ctx, box, int, successFP, errorFP) → 5 IntPtr/int slots + void
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, IntPtr, void>", result);
    }

    #endregion

    #region EmitAsyncThrowingClosureMarshallingSetup

    [Fact]
    public void AsyncSetup_VoidReturn_EmitsVoidState()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
            csWriter, "doWork", "handler", closureTypeSpec, closureHandler,
            "$s_mangled");

        var result = output.ToString();
        Assert.Contains("AsyncThrowingClosureStateVoid", result);
        Assert.Contains("AsyncFunc = handler", result);
        Assert.Contains("GCHandle.Alloc", result);
        Assert.Contains("GCHandle.ToIntPtr", result);
    }

    [Fact]
    public void AsyncSetup_TypedReturn_EmitsGenericState()
    {
        var typeDatabase = CreateTypeDatabaseWithLoader();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("TestModule.Loader"))
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
            csWriter, "load", "handler", closureTypeSpec, closureHandler,
            "$s_mangled");

        var result = output.ToString();
        Assert.Contains("AsyncThrowingClosureState<", result);
        Assert.Contains("GCHandle.Alloc", result);
    }

    [Fact]
    public void AsyncSetup_ContextPtrVariable_Named()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
            csWriter, "doWork", "myCallback", closureTypeSpec, closureHandler,
            "$s_mangled");

        var result = output.ToString();
        // Variable names derive from parameter name
        Assert.Contains("myCallbackState", result);
        Assert.Contains("myCallbackHandle", result);
        Assert.Contains("myCallbackContextPtr", result);
    }

    #endregion

    #region Helpers

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithLoader()
    {
        var typeDatabase = CreateTypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion
}
