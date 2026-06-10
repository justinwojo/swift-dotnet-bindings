// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for CancellationToken support on async methods:
/// - CancellationTaskEmitter (Swift infrastructure + C# P/Invoke dedup)
/// - WrapperEmitter.Async.cs (signature, task store, registration, callback cleanup)
/// </summary>
public class CancellationTokenEmitterTests
{
    #region CancellationTaskEmitter Unit Tests

    [Fact]
    public void EmitIfNeeded_EmitsSwiftInfrastructureOnce()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        var emitted = CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);
        Assert.True(emitted);

        var output = sw.ToString();
        Assert.Contains("_SBWTaskEntry", output);
        Assert.Contains("_sbwActiveTasks", output);
        Assert.Contains("_sbwTaskLock", output);
        Assert.Contains("@_cdecl(\"SBW_CancelTask_TestModule\")", output);
    }

    [Fact]
    public void EmitIfNeeded_SecondCallReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);
        var emittedSecond = CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);

        Assert.False(emittedSecond);
    }

    [Fact]
    public void EmitIfNeeded_TaskEntryIsFinalClass()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);
        var output = sw.ToString();

        Assert.Contains("private final class _SBWTaskEntry", output);
        Assert.Contains("var task: Task<Void, Never>?", output);
    }

    [Fact]
    public void EmitIfNeeded_CancelFunctionLooksUpAndCancelsTask()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);
        var output = sw.ToString();

        Assert.Contains("_sbwTaskLock.lock()", output);
        Assert.Contains("_sbwActiveTasks[taskId]", output);
        Assert.Contains("_sbwTaskLock.unlock()", output);
        Assert.Contains("entry?.task?.cancel()", output);
    }

    [Fact]
    public void EmitIfNeeded_EmitsAsyncSafeHelperFunctions()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx);
        var output = sw.ToString();

        // Helper functions wrap NSLock operations so they can be safely called
        // from async contexts (Swift 6 marks NSLock.lock/unlock as @available(*, noasync))
        Assert.Contains("private func _sbwRegisterTask(_ taskId: Int64, _ entry: _SBWTaskEntry)", output);
        Assert.Contains("private func _sbwUnregisterTask(_ taskId: Int64)", output);
    }

    [Fact]
    public void GetCancelSymbolName_ReturnsModuleSpecificName()
    {
        Assert.Equal("SBW_CancelTask_ImagePipeline", CancellationTaskEmitter.GetCancelSymbolName("ImagePipeline"));
        Assert.Equal("SBW_CancelTask_TestModule", CancellationTaskEmitter.GetCancelSymbolName("TestModule"));
    }

    [Fact]
    public void PerTypePInvokeDedup_TracksCorrectly()
    {
        var ctx = new ModuleEmissionContext();

        Assert.False(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline", ctx));
        CancellationTaskEmitter.MarkCancelPInvokeEmittedForType("TestModule.Pipeline", ctx);
        Assert.True(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline", ctx));
    }

    [Fact]
    public void FreshContext_HasCleanState()
    {
        var ctx1 = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule", ctx1);
        CancellationTaskEmitter.MarkCancelPInvokeEmittedForType("TestModule.Pipeline", ctx1);
        Assert.True(CancellationTaskEmitter.IsEmitted(ctx1));
        Assert.True(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline", ctx1));

        // Fresh context should be clean
        var ctx2 = new ModuleEmissionContext();

        Assert.False(CancellationTaskEmitter.IsEmitted(ctx2));
        Assert.False(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline", ctx2));
        Assert.Null(CancellationTaskEmitter.GetCurrentModuleName(ctx2));
    }

    #endregion

    #region C# Signature Tests (using full emission pipeline)

    [Fact]
    public void AsyncMethod_HasCancellationTokenParam()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("global::System.Threading.CancellationToken cancellationToken = default", csOutput);
    }

    [Fact]
    public void AsyncVoidMethod_HasCancellationTokenParam()
    {
        var (csOutput, _) = GenerateAsyncVoidMethod();
        Assert.Contains("global::System.Threading.CancellationToken cancellationToken = default", csOutput);
    }

    [Fact]
    public void SyncMethod_DoesNotHaveCancellationTokenParam()
    {
        var (csOutput, _) = GenerateSyncMethod();
        Assert.DoesNotContain("CancellationToken", csOutput);
    }

    [Fact]
    public void AsyncStaticMethod_HasCancellationTokenParam()
    {
        var (csOutput, _) = GenerateAsyncStaticMethod();
        Assert.Contains("global::System.Threading.CancellationToken cancellationToken = default", csOutput);
    }

    #endregion

    #region Swift Task Storage Tests

    [Fact]
    public void AsyncWrapper_StoresEntryBeforeTask()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();

        int entryIdx = swiftOutput.IndexOf("let _entry = _SBWTaskEntry()");
        int taskIdx = swiftOutput.IndexOf("_entry.task = Task {");
        Assert.True(entryIdx >= 0, "Should create _SBWTaskEntry");
        Assert.True(taskIdx >= 0, "Should assign task to entry");
        Assert.True(entryIdx < taskIdx, "_SBWTaskEntry should be created before Task assignment");
    }

    [Fact]
    public void AsyncWrapper_StoresEntryInDictionary()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        // Registry key is the monotonic _sbwCancelKey, NOT the recyclable GCHandle
        // context (_sbwTask). See the Cancellation Key Recycle Fix region below for the rationale.
        Assert.Contains("_sbwRegisterTask(_sbwCancelKey, _entry)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_DefersRemovalFromDictionary()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        // Unregister keyed by the same monotonic _sbwCancelKey used to register.
        Assert.Contains("_sbwUnregisterTask(_sbwCancelKey)", swiftOutput);
        Assert.Contains("defer {", swiftOutput);
    }

    #endregion

    #region C# Registration Tests

    [Fact]
    public void AsyncMethod_EmitsPreCancelCheck()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("cancellationToken.IsCancellationRequested", csOutput);
        Assert.Contains("Task.FromCanceled", csOutput);
    }

    [Fact]
    public void AsyncMethod_EmitsRegistration()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("cancellationToken.CanBeCanceled", csOutput);
        Assert.Contains("cancellationToken.Register(", csOutput);
        Assert.Contains("SBW_CancelTask(id)", csOutput);
        Assert.Contains("tcs.TrySetCanceled(token)", csOutput);
    }

    [Fact]
    public void AsyncMethod_EmitsSBWCancelTaskPInvoke()
    {
        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("[global::System.Runtime.InteropServices.LibraryImport(", csOutput);
        Assert.Contains("SBW_CancelTask_TestModule", csOutput);
        Assert.Contains("private static partial void SBW_CancelTask(long taskId)", csOutput);
    }

    [Fact]
    public void AsyncMethod_StoresRegistrationInHolder()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("CancellationRegistrationHolder(_cancelRegistration, cancellationToken)", csOutput);
    }

    #endregion

    #region Error Callback Tests

    [Fact]
    public void AsyncWrapper_ErrorCallbackHasIsCancellationParam()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("int isCancellation", csOutput);
    }

    [Fact]
    public void AsyncWrapper_SwiftCatchEmitsIsCancelled()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        Assert.Contains("let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_ErrorCallbackHandlesCancellation()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("if (isCancellation != 0)", csOutput);
        Assert.Contains("holderTcs.TrySetCanceled(cancelToken)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ErrorCallbackDisposesRegistrationOnError()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // The registration is freed via holder cleanup (which disposes it — proven by
        // SwiftAsyncCallHolderTests.Cleanup_DisposesCancellationRegistration), and that cleanup
        // runs before the error callback faults the Task.
        AssertHolderCleanupPrecedes(csOutput, "holderTcs.TrySetException(exception)");
    }

    [Fact]
    public void AsyncWrapper_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // The registration is stored in the holder so cleanup can find and dispose it...
        Assert.Contains("new CancellationRegistrationHolder(_cancelRegistration, cancellationToken)", csOutput);
        // ...and the success path runs holder cleanup before completing the Task with the result.
        AssertHolderCleanupPrecedes(csOutput, "holderTcs.TrySetResult");
    }

    #endregion

    #region Holder Tests

    [Fact]
    public void AsyncMethod_HolderHasNullSlotForRegistration()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("null!", csOutput);
    }

    [Fact]
    public void AsyncStaticMethod_UsesHolderArray()
    {
        var (csOutput, _) = GenerateAsyncStaticMethod();
        Assert.Contains("object[] _asyncCallHolder", csOutput);
        Assert.Contains("null!", csOutput);
    }

    #endregion

    #region Swift Error Callback Signature Tests

    [Fact]
    public void AsyncWrapper_UntypedThrows_ErrorCallbackUsesUnifiedSixParam()
    {
        // Unified wire format: typed-throws, plain-throws cascade, and untyped
        // throws all share a single 6-param shape
        // (errorPtr?, errorSize, msgPtr?, isCancellation, _sbwTask, errorTypeId).
        // Untyped fixture has no registered error types, so the body passes
        // (nil, 0, _msgPtr, _isCancelled, _sbwTask, 0).
        var (csOutput, swiftOutput) = GenerateAsyncMethod();
        Assert.Contains("_isCancelled, _sbwTask, 0", swiftOutput);
        Assert.Contains("IntPtr, nint, IntPtr, int, IntPtr, int, void>", csOutput);
    }

    #endregion

    #region Registration Disposal in All Async Return Shapes

    [Fact]
    public void AsyncStringReturn_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncStringMethod();
        // String return uses EmitAsyncWrapperForString — its success path must also wire holder
        // cleanup (which disposes the registration) so this separate emitter cannot drift and leak.
        Assert.Contains("new CancellationRegistrationHolder(_cancelRegistration, cancellationToken)", csOutput);
        AssertHolderCleanupPrecedes(csOutput, "TrySetResult");
    }

    [Fact]
    public void AsyncComplexReturn_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncComplexReturnMethod();
        // Non-frozen return uses EmitAsyncWrapperForComplexType — same separate-emitter drift guard:
        // its success path runs holder cleanup (disposes the registration) before completing.
        Assert.Contains("new CancellationRegistrationHolder(_cancelRegistration, cancellationToken)", csOutput);
        AssertHolderCleanupPrecedes(csOutput, "TrySetResult");
    }

    #endregion

    #region Typed Throws Cancellation Free

    [Fact]
    public void AsyncTypedThrows_CancellationPath_FreesErrorBuffer()
    {
        var (csOutput, _) = GenerateAsyncTypedThrowsMethod();
        // Typed throws cancellation path must free the Swift-allocated error buffer
        // The SBW_Free(errorPtr) should appear in the isCancellation block
        Assert.Contains("SBW_Free(errorPtr)", csOutput);
        // Verify both cancellation and non-cancellation paths have SBW_Free
        var lines = csOutput.Split('\n');
        int freeCount = lines.Count(l => l.Contains("SBW_Free(errorPtr)"));
        // At least 2: one in cancellation block, one in non-cancellation MarshalFromSwift block
        Assert.True(freeCount >= 2, $"Expected at least 2 SBW_Free(errorPtr) calls, found {freeCount}");
    }

    [Fact]
    public void AsyncUntypedThrows_CancellationPath_DoesNotFreErrorPtr()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // Untyped throws has no errorPtr parameter — SBW_Free(errorPtr) should not appear
        Assert.DoesNotContain("SBW_Free(errorPtr)", csOutput);
    }

    #endregion

    #region Projected Key Tests — Async Methods Include CancellationToken

    [Fact]
    public void AsyncMethod_ProjectedKeyIncludesCancellationToken()
    {
        // BaseHandler.GetProjectedCSharpMethodKey adds CancellationToken for async methods.
        // Test via reflection since the method is private static.
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl(moduleDecl);
        moduleDecl.Types.Add(parentDecl);

        var methodDecl = new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$s10TestModule8PipelineC9fetchDataSiyYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateBasicTypeDatabase(parentDecl);
        var key = InvokeGetProjectedCSharpMethodKey(methodDecl, typeDatabase);

        Assert.Contains("System.Threading.CancellationToken", key);
    }

    [Fact]
    public void SyncMethod_ProjectedKeyDoesNotIncludeCancellationToken()
    {
        // Sync methods should NOT have CancellationToken in their projected key.
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl(moduleDecl);
        moduleDecl.Types.Add(parentDecl);

        var methodDecl = new MethodDecl
        {
            Name = "getData",
            MangledName = "$s10TestModule8PipelineC7getDataSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateBasicTypeDatabase(parentDecl);
        var key = InvokeGetProjectedCSharpMethodKey(methodDecl, typeDatabase);

        Assert.DoesNotContain("CancellationToken", key);
    }

    /// <summary>
    /// Invokes BaseHandler.GetProjectedCSharpMethodKey via reflection (private static).
    /// </summary>
    private static string InvokeGetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var method = typeof(BaseHandler).GetMethod(
            "GetProjectedCSharpMethodKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        // Args: methodDecl, typeDatabase, logger, siblingPropertyNames, treatAsClosureTombstone.
        return (string)method!.Invoke(null, new object[] { methodDecl, typeDatabase, null!, null!, false })!;
    }

    #endregion

    #region Cancellation Key Recycle Fix

    // The Swift cancellation registry (_sbwActiveTasks) must be keyed by a value that is
    // NEVER reused while an entry is live. Previously the key was the GCHandle pointer
    // value (also reused as the callback context), but GCHandle cookies are recycled after
    // Free() — a completing task's deferred unregister could then evict a newer task that
    // reused the cookie, and a racing cancellation could cancel unrelated work. The fix
    // separates the registry key (a process-wide monotonic _sbwCancelKey) from the callback
    // context (the GCHandle, still recovered via GCHandle.FromIntPtr).

    [Fact]
    public void AsyncWrapper_RegistersWithMonotonicCancelKeyNotContext()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        Assert.Contains("_sbwRegisterTask(_sbwCancelKey, _entry)", swiftOutput);
        Assert.Contains("_sbwUnregisterTask(_sbwCancelKey)", swiftOutput);
        // The recyclable context must NOT be used as the registry key.
        Assert.DoesNotContain("_sbwRegisterTask(_sbwTask", swiftOutput);
        Assert.DoesNotContain("_sbwUnregisterTask(_sbwTask)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_SwiftSignatureDeclaresSeparateCancelKeyParam()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        // Both values cross the boundary: _sbwTask (context) and _sbwCancelKey (registry key).
        Assert.Contains("_sbwCancelKey: Int64", swiftOutput);
        Assert.Contains("_sbwTask: Int64", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_CallbackContextStillUsesSbwTaskNotCancelKey()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        // The success/error callbacks recover the GCHandle holder, so they must forward the
        // context (_sbwTask) — never the registry key — back to C#.
        Assert.Contains("_sbwTask)", swiftOutput);
        Assert.DoesNotContain("callback(_sbwCancelKey)", swiftOutput);
    }

    [Fact]
    public void AsyncMethod_ComputesMonotonicCancelKeyDistinctFromHandle()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // The registry key comes from a process-wide monotonic counter...
        Assert.Contains("SwiftAsyncCancellation.NextCancelKey()", csOutput);
        // ...NOT from the recyclable GCHandle pointer value.
        Assert.DoesNotContain("(long)(IntPtr)handle", csOutput);
        // The GCHandle is still passed as the opaque callback context.
        Assert.Contains("GCHandle.ToIntPtr(handle)", csOutput);
    }

    [Fact]
    public void AsyncMethod_CancelRegistrationCapturesMonotonicKey()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // The cancellation registration must cancel by the monotonic key.
        Assert.Contains("cancellationToken, _sbwCancelKey)", csOutput);
        Assert.Contains("SBW_CancelTask(id)", csOutput);
    }

    [Fact]
    public void AsyncMethod_LaunchPInvokeForwardsCancelKey()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        // The launch P/Invoke declares the context (IntPtr handle) followed by the
        // monotonic cancel key (long _sbwCancelKey); the call site passes the key.
        // Collapse runs of spaces: empty-modifier params render with a leading space
        // (see ParameterSignatureTests), so the join emits a double space before `long`.
        var normalized = System.Text.RegularExpressions.Regex.Replace(csOutput, " +", " ");
        Assert.Contains("IntPtr handle, long _sbwCancelKey", normalized);
        Assert.Contains("long _sbwCancelKey = SwiftAsyncCancellation.NextCancelKey()", csOutput);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Asserts that holder cleanup runs on the termination path that reaches
    /// <paramref name="completionCall"/> — i.e. a <c>SwiftAsyncCallHolder.Cleanup(...)</c> call
    /// textually precedes it. The cleanup helper is what disposes the cancellation registration
    /// (proven exactly-once by <c>SwiftAsyncCallHolderTests.Cleanup_DisposesCancellationRegistration</c>),
    /// so this is the faithful per-return-shape "registration is freed on this path" check after
    /// the disposal was extracted out of the inline callback bodies.
    /// </summary>
    private static void AssertHolderCleanupPrecedes(string csOutput, string completionCall)
    {
        const string cleanup = "global::Swift.Runtime.SwiftAsyncCallHolder.Cleanup(";
        int completionIdx = csOutput.IndexOf(completionCall, StringComparison.Ordinal);
        Assert.True(completionIdx >= 0, $"expected completion call '{completionCall}' in generated output");
        int cleanupIdx = csOutput.LastIndexOf(cleanup, completionIdx, StringComparison.Ordinal);
        Assert.True(cleanupIdx >= 0,
            $"holder cleanup (disposes the cancellation registration) must run before '{completionCall}'");
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(ModuleDecl moduleDecl)
    {
        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        return parentDecl;
    }

    private static TypeDatabase CreateBasicTypeDatabase(ClassDecl parentDecl)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    /// <summary>
    /// Generates an async instance method on a class (non-void return).
    /// Uses the same proven pattern as AsyncSwiftWrapperTests.GenerateAsyncMethodWithComplexReturn.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        // Return a struct type (same as existing AsyncSwiftWrapperTests.GenerateAsyncMethodWithComplexReturn)
        var returnTypeName = "TestModule.DataResult";
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = "$s10TestModule8PipelineC11fetchResult_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        // Register the return type as a struct
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule10DataResultVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates an async instance method returning void.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncVoidMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule8PipelineC7process_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates a synchronous instance method (not async).
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateSyncMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule0A6StructVMa",
            MangledName = "$s10TestModule0A6StructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "getValue",
            MangledName = "$s10TestModule0A6StructV8getValueSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestStruct"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        module.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates an async static method.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncStaticMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var returnTypeName = "TestModule.DataResult";
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchCount",
            MangledName = "$s10TestModule8PipelineC10fetchCountSiyYaKFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule10DataResultVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates an async instance method returning String (exercises EmitAsyncWrapperForString).
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncStringMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "getName",
            MangledName = "$s10TestModule8PipelineC7getName_tSSYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        typeDatabase.AddModuleDatabase(module);

        // Load Swift database for String
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates an async instance method returning a non-frozen struct (exercises EmitAsyncWrapperForComplexType).
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncComplexReturnMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        // Return a non-frozen struct (triggers ComplexType emitter with ClassWithOpaquePayload)
        var returnTypeName = "TestModule.OpaqueResult";
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchOpaque",
            MangledName = "$s10TestModule8PipelineC12fetchOpaque_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        // Register as non-frozen (RequiresMemoryManagement → ClassWithOpaquePayload → ComplexType emitter)
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "OpaqueResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule12OpaqueResultVMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Generates an async instance method with typed throws (exercises typed error callback path).
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncTypedThrowsMethod()
    {
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var returnTypeName = "TestModule.DataResult";
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = "$s10TestModule8PipelineC11fetchResult_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public,
            ThrownErrorType = TypeSpecParser.Parse("TestModule.ParseError")
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule10DataResultVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var errorSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError");
        module.RegisterType(errorSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ParseError"),
            SwiftTypeName = errorSwiftName,
            MetadataAccessor = "$s10TestModule10ParseErrorOMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Enum
        });

        typeDatabase.AddModuleDatabase(module);

        return EmitMethod(methodDecl, typeDatabase);
    }

    /// <summary>
    /// Common emission logic — passes method through MethodHandler.Marshal → Emit pipeline.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
