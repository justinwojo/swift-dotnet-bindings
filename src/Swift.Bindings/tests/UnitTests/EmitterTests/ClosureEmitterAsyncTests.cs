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
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(
            csWriter, "doWork", "handler", "$s_mangled");

        var result = output.ToString();
        Assert.Contains("private static unsafe readonly", result);
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>", result);
        Assert.Contains("_Start", result);
    }

    [Fact]
    public void AsyncCallbackPointer_NameDerivedFromMethodAndParam()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(
            csWriter, "fetchResource", "completion", "$s_xyz");

        var result = output.ToString();
        // The callback name is derived from method name + parameter name
        Assert.Contains("fetchResource", result);
        Assert.Contains("completion", result);
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
