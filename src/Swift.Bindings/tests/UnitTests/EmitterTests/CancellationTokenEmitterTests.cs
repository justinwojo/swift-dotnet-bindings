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
        CancellationTaskEmitter.ResetForModule();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        var emitted = CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");
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
        CancellationTaskEmitter.ResetForModule();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");
        var emittedSecond = CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");

        Assert.False(emittedSecond);
    }

    [Fact]
    public void EmitIfNeeded_TaskEntryIsFinalClass()
    {
        CancellationTaskEmitter.ResetForModule();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");
        var output = sw.ToString();

        Assert.Contains("private final class _SBWTaskEntry", output);
        Assert.Contains("var task: Task<Void, Never>?", output);
    }

    [Fact]
    public void EmitIfNeeded_CancelFunctionLooksUpAndCancelsTask()
    {
        CancellationTaskEmitter.ResetForModule();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");
        var output = sw.ToString();

        Assert.Contains("_sbwTaskLock.lock()", output);
        Assert.Contains("_sbwActiveTasks[taskId]", output);
        Assert.Contains("_sbwTaskLock.unlock()", output);
        Assert.Contains("entry?.task?.cancel()", output);
    }

    [Fact]
    public void GetCancelSymbolName_ReturnsModuleSpecificName()
    {
        Assert.Equal("SBW_CancelTask_Nuke", CancellationTaskEmitter.GetCancelSymbolName("Nuke"));
        Assert.Equal("SBW_CancelTask_TestModule", CancellationTaskEmitter.GetCancelSymbolName("TestModule"));
    }

    [Fact]
    public void PerTypePInvokeDedup_TracksCorrectly()
    {
        CancellationTaskEmitter.ResetForModule();

        Assert.False(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline"));
        CancellationTaskEmitter.MarkCancelPInvokeEmittedForType("TestModule.Pipeline");
        Assert.True(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline"));
    }

    [Fact]
    public void ResetForModule_ClearsAllState()
    {
        CancellationTaskEmitter.ResetForModule();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        CancellationTaskEmitter.EmitIfNeeded(writer, "TestModule");
        CancellationTaskEmitter.MarkCancelPInvokeEmittedForType("TestModule.Pipeline");
        Assert.True(CancellationTaskEmitter.IsEmitted);
        Assert.True(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline"));

        CancellationTaskEmitter.ResetForModule();

        Assert.False(CancellationTaskEmitter.IsEmitted);
        Assert.False(CancellationTaskEmitter.HasCancelPInvokeForType("TestModule.Pipeline"));
        Assert.Null(CancellationTaskEmitter.CurrentModuleName);
    }

    #endregion

    #region C# Signature Tests (using full emission pipeline)

    [Fact]
    public void AsyncMethod_HasCancellationTokenParam()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", csOutput);
    }

    [Fact]
    public void AsyncVoidMethod_HasCancellationTokenParam()
    {
        var (csOutput, _) = GenerateAsyncVoidMethod();
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", csOutput);
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
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", csOutput);
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
        Assert.Contains("_sbwActiveTasks[task] = _entry", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_DefersRemovalFromDictionary()
    {
        var (_, swiftOutput) = GenerateAsyncMethod();
        Assert.Contains("_sbwActiveTasks.removeValue(forKey: task)", swiftOutput);
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
        CancellationTaskEmitter.ResetForModule();
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("[System.Runtime.InteropServices.DllImport(", csOutput);
        Assert.Contains("SBW_CancelTask_TestModule", csOutput);
        Assert.Contains("private static extern void SBW_CancelTask(long taskId)", csOutput);
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
        Assert.Contains("cancelReg.Registration.Dispose()", csOutput);
    }

    [Fact]
    public void AsyncWrapper_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncMethod();
        Assert.Contains("CancellationRegistrationHolder cancelReg", csOutput);
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
    public void AsyncWrapper_UntypedThrows_ErrorCallbackHas3Params()
    {
        var (csOutput, swiftOutput) = GenerateAsyncMethod();
        Assert.Contains("_isCancelled, task", swiftOutput);
        Assert.Contains("IntPtr, int, IntPtr, void>", csOutput);
    }

    #endregion

    #region Registration Disposal in All Async Return Shapes

    [Fact]
    public void AsyncStringReturn_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncStringMethod();
        // String return uses EmitAsyncWrapperForString — verify registration disposal in success callback
        Assert.Contains("CancellationRegistrationHolder cancelReg", csOutput);
        Assert.Contains("cancelReg.Registration.Dispose()", csOutput);
    }

    [Fact]
    public void AsyncComplexReturn_SuccessCallbackDisposesRegistration()
    {
        var (csOutput, _) = GenerateAsyncComplexReturnMethod();
        // Non-frozen return uses EmitAsyncWrapperForComplexType — verify registration disposal in success callback
        Assert.Contains("CancellationRegistrationHolder cancelReg", csOutput);
        Assert.Contains("cancelReg.Registration.Dispose()", csOutput);
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

    #region Helper Methods

    /// <summary>
    /// Generates an async instance method on a class (non-void return).
    /// Uses the same proven pattern as AsyncSwiftWrapperTests.GenerateAsyncMethodWithComplexReturn.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethod()
    {
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        // Register the return type as a struct
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResult"),
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
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
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
        CancellationTaskEmitter.ResetForModule();

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
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "TestStruct"),
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
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResult"),
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
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
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
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        // Register as non-frozen (RequiresMemoryManagement → ClassWithOpaquePayload → ComplexType emitter)
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "OpaqueResult"),
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
        CancellationTaskEmitter.ResetForModule();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule10DataResultVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var errorSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError");
        module.RegisterType(errorSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ParseError"),
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
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
