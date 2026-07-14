// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncMethodGenericBridgeEmitter — async/throws counterpart to
/// <see cref="MethodGenericBridgeEmitter"/>. Covers the StoreKit2-shaped pattern:
/// class-bound non-CSM generic + async + (optionally) throws.
/// </summary>
public class AsyncMethodGenericBridgeEmitterTests
{
    #region TryEmit gates

    [Fact]
    public void TryEmit_Constructor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("init", isConstructor: true);
        method.IsAsync = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_Accessor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("getValue");
        method.IsAccessor = true;
        method.IsAsync = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_Sync_ReturnsFalse()
    {
        // Sync methods are owned by MethodGenericBridgeEmitter; async emitter must
        // bail so the bridge dispatch table can fall through to the sync adapter.
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = false;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent));
    }

    [Fact]
    public void TryEmit_AlreadyUsesWrapperLibrary_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.UsesWrapperLibrary = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_NoParentDecl_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_GenericParent_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Container");
        parent.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new(), new())
        };
        method.ParentDecl = parent;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent));
    }

    [Fact]
    public void TryEmit_NoMethodOwnGenericParams_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var env = CreateMethodEnvironment(method);

        Assert.False(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent));
    }

    #endregion

    #region FindEligibleGenericParam

    [Fact]
    public void FindEligible_SingleProtocolConstraint_ReturnsInfo()
    {
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var typeDatabase = CreateTypeDatabase();

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, typeDatabase);

        Assert.NotNull(result);
        Assert.Equal("τ_1_0", result!.Param.TypeName);
        Assert.Equal("TestModule.Describable", result.ConstraintProtocol.ModuleQualifiedName);
    }

    [Fact]
    public void FindEligible_MultipleOwnParams_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("a", new NamedTypeSpec("τ_1_0"), moduleDecl),
                CreateArg("b", new NamedTypeSpec("τ_1_1"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
                new GenericArgumentDecl("τ_1_1", "U", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_NoProtocolConformance_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new(), new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_NoAnyObjectBound_ReturnsNull()
    {
        // Class-bound is required — opening via Unmanaged<AnyObject>.fromOpaque
        // requires a heap allocation. Struct conformers would crash.
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_MultipleNonAnyObjectProtocols_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Printable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Swift.Equatable")]
    [InlineData("Swift.Hashable")]
    [InlineData("Swift.Comparable")]
    [InlineData("Swift.Codable")]
    [InlineData("Swift.Sequence")]
    [InlineData("Swift.Collection")]
    public void FindEligible_SelfRequirementProtocol_ReturnsNull(string protocolName)
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName(protocolName), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_GenericParamInsideContainer_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        // Array<τ_1_0> — generic param is nested, not direct position
        var arraySpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("τ_1_0"));

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule7process_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("items", arraySpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = AsyncMethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());
        Assert.Null(result);
    }

    #endregion

    #region IsEligible

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsTrue()
    {
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.True(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncThrowsMethod_ReturnsTrue()
    {
        // Throws is the StoreKit2 shape — must be eligible.
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.Throws = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.True(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_SyncMethod_ReturnsFalse()
    {
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = false;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.False(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_GenericParamInReturnType_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("Processor");
        var method = new MethodDecl
        {
            Name = "transform",
            MangledName = "$s10TestModule9transform_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_1_0"), moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        Assert.False(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_NoXCFrameworkMode_ReturnsFalse()
    {
        // No AsyncLibraryName set → not in xcframework mode → bridge cannot be emitted.
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        // Intentionally leave AsyncLibraryName null

        Assert.False(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_StringReturn_ReturnsFalse()
    {
        // Swift.String is non-generic, so it slips past the ContainsGenericParameters gate, but its
        // public projection is `string` (not an ISwiftObject), so it cannot ride the ComplexValue
        // value-carrier ABI — the completion-callback arms would emit SwiftObjectHelper<string> /
        // MarshalFromSwift<string>, which do not compile and would not release the carrier's String
        // storage. ClassifyReturnKind must bail. Swapping ONLY the return type of an otherwise
        // eligible method flips eligibility, proving String is the sole differentiator.
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        // Sanity: the base method (void return, class-bound generic param) IS eligible.
        Assert.True(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));

        // The ONLY change is the return type → Swift.String, which must make it ineligible.
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.String"), method.ModuleDecl!);
        Assert.False(AsyncMethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    #endregion

    #region TryEmit: eligible method emits bridge — async, no-throws, void return

    [Fact]
    public void TryEmit_EligibleAsyncVoidMethod_EmitsCdeclWrapperWithXmaSuffix()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        Assert.True(handled);
        Assert.True(method.WasEmitted);
        Assert.True(method.UsesWrapperLibrary);
        Assert.True(method.UsesFreeFunctionWrapper);
        Assert.True(method.HasGenericClosureBridge);

        var swiftResult = swiftOutput.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_Processor_process_", swiftResult);
        Assert.Contains("_XMA\")", swiftResult);
        Assert.Contains("UnsafeRawPointer", swiftResult);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(", swiftResult);
        Assert.Contains("as! any TestModule.Describable)", swiftResult);
        // Async harness shape
        Assert.Contains("_SBWTaskEntry()", swiftResult);
        Assert.Contains("_sbwRegisterTask", swiftResult);
        Assert.Contains("Task {", swiftResult);
        Assert.Contains("await", swiftResult);
        // Void return → callback takes only taskId (Int64)
        Assert.Contains("@convention(c) (Int64) -> Void", swiftResult);
        Assert.Contains("callback(_sbwTask)", swiftResult);
    }

    [Fact]
    public void TryEmit_EligibleAsyncVoidMethod_EmitsCSharpHarness()
    {
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        // C# async harness must include the standard pieces.
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("CallConvCdecl", csResult);
        Assert.Contains("UnmanagedCallersOnly", csResult);
        Assert.Contains("delegate* unmanaged[Cdecl]", csResult);
        Assert.Contains("TaskCompletionSource", csResult);
        Assert.Contains("GCHandle", csResult);
        Assert.Contains("SBW_CancelTask", csResult);
        Assert.Contains("CancellationToken cancellationToken", csResult);
        // Generic argument is passed as ISwiftObject and routed through SwiftHandle
        Assert.Contains("global::Swift.Runtime.ISwiftObject", csResult);
        Assert.Contains(".SwiftHandle", csResult);
    }

    #endregion

    #region Cancellation Key Recycle Fix

    [Fact]
    public void TryEmit_SwiftWrapper_RegistersWithMonotonicCancelKeyNotContext()
    {
        var (csWriter, swiftWriter, _, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var swiftResult = swiftOutput.ToString();
        // The registry is keyed by the monotonic cancel key, not the recyclable GCHandle context.
        Assert.Contains("_sbwRegisterTask(_sbwCancelKey, _entry)", swiftResult);
        Assert.Contains("_sbwUnregisterTask(_sbwCancelKey)", swiftResult);
        Assert.DoesNotContain("_sbwRegisterTask(_sbwTask", swiftResult);
        Assert.DoesNotContain("_sbwUnregisterTask(_sbwTask)", swiftResult);
        // The callback context is still the GCHandle-derived _sbwTask (unchanged).
        Assert.Contains("callback(_sbwTask)", swiftResult);
    }

    [Fact]
    public void TryEmit_SwiftWrapper_DeclaresSeparateContextAndCancelKeyParams()
    {
        var (csWriter, swiftWriter, _, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var swiftResult = swiftOutput.ToString();
        // Both the GCHandle context and the monotonic cancel key are declared as @_cdecl params.
        Assert.Contains("_ _sbwTask: Int64", swiftResult);
        Assert.Contains("_ _sbwCancelKey: Int64", swiftResult);
    }

    [Fact]
    public void TryEmit_CSharpHarness_DeclaresAndForwardsCancelKey()
    {
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        // P/Invoke declares the GCHandle context (taskId) followed by the monotonic key (cancelKey).
        Assert.Contains("long taskId, long cancelKey", csResult);
        // The harness computes the monotonic key and cancels by it (not the GCHandle cookie).
        Assert.Contains("long _sbwCancelKey = SwiftAsyncCancellation.NextCancelKey();", csResult);
        Assert.Contains("cancellationToken, _sbwCancelKey)", csResult);
        // The GCHandle cookie is still forwarded as the opaque callback context.
        Assert.Contains("(long)(IntPtr)handle", csResult);
    }

    [Fact]
    public void TryEmit_CSharpHarness_ForegroundCatchReclaimsTombstone()
    {
        // Finding 39 WINDOW A leak closure (generic-bridge path): when the foreground throws
        // before the P/Invoke launches the Swift task, the Swift `defer { _sbwUnregisterTask }`
        // never runs, so a cancel that landed in the register window would strand its tombstone.
        // The catch declares + calls the unregister entry point (keyed by the monotonic key) to
        // reclaim it after freeing the GCHandle.
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        Assert.Contains("private static partial void SBW_UnregisterTask(long taskId)", csResult);
        Assert.Contains("SBW_UnregisterTask(_sbwCancelKey)", csResult);

        int handleFreeIdx = csResult.IndexOf("handle.Free();", System.StringComparison.Ordinal);
        int unregisterIdx = csResult.IndexOf("SBW_UnregisterTask(_sbwCancelKey)", System.StringComparison.Ordinal);
        Assert.True(handleFreeIdx >= 0 && unregisterIdx > handleFreeIdx, "reclaim runs after handle.Free() on the catch path");
    }

    #endregion

    #region TryEmit: throws path

    [Fact]
    public void TryEmit_AsyncThrows_EmitsErrorCallbackParams()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.Throws = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        Assert.True(handled);

        var swiftResult = swiftOutput.ToString();
        // Swift wrapper must accept the cascade error callback.
        Assert.Contains(
            "errorCallback: @convention(c) (UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32) -> Void",
            swiftResult);
        // Try/catch around the awaited call.
        Assert.Contains("try await", swiftResult);
        Assert.Contains("} catch {", swiftResult);

        var csResult = csOutput.ToString();
        // Error-callback delegate field + UnmanagedCallersOnly handler.
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, int, long, int, void>", csResult);
        Assert.Contains("Marshal.PtrToStringUTF8", csResult);
        Assert.Contains("TrySetException", csResult);
        Assert.Contains("TrySetCanceled", csResult);
    }

    [Fact]
    public void TryEmit_AsyncThrows_RemappedNamespace_ErrorCallbackUsesFullyQualifiedHelperReference()
    {
        // AMGBE's error-callback path must route through the shared helper-name resolver
        // (same as AsyncHarnessEmitter) so a NamespacePattern remap cannot diverge the
        // helper-class cross-reference from the namespace the registry is emitted into.
        // StoreKit → StoreKit2 is the production remap shape.
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.Throws = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext
        {
            ResolvedNamespace = "StoreKit2",
        };
        // Register at least one Error-conforming type so the cascade helper path is taken
        // (empty ErrorTypeOrder falls back to plain SwiftException and never names the helper).
        ctx.RegisterErrorTypeId("TestModule.SomeError");

        Assert.True(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx));

        var csResult = csOutput.ToString();
        var expectedHelperRef = ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference(
            moduleName: "TestModule", resolvedNamespace: "StoreKit2");
        Assert.Equal("global::StoreKit2._SbwModuleErrorRegistry_TestModule", expectedHelperRef);
        Assert.Contains($"{expectedHelperRef}.CreateException(", csResult);
        // Identity-namespace path (pre-remap) must not appear — that is the bypass shape.
        Assert.DoesNotContain("global::TestModule._SbwModuleErrorRegistry_TestModule", csResult);
        // Un-qualified bare helper name would bind against the enclosing type's namespace,
        // not the remapped registry namespace (the pre-fix production form).
        Assert.DoesNotContain(
            "var exception = _SbwModuleErrorRegistry_TestModule.CreateException", csResult);
    }

    [Fact]
    public void TryEmit_AsyncNoThrows_DoesNotEmitErrorCallback()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.Throws = false;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var swiftResult = swiftOutput.ToString();
        Assert.DoesNotContain("errorCallback", swiftResult);
        Assert.DoesNotContain("try await", swiftResult);
        Assert.Contains("await", swiftResult);

        var csResult = csOutput.ToString();
        Assert.DoesNotContain("errorCallback", csResult);
        // No-throws → no Swift error-callback exception reporting on the real TCS.
        Assert.DoesNotContain("_tcs.TrySetException", csResult);
        // The graceful-fault catch wraps EVERY async UCO callback body (throwing or not), so a
        // managed exception in the callback faults the Task instead of unwinding into Swift
        // (SIGABRT). That path's `__faultTcs.TrySetException(__ex)` is expected here and is
        // distinct from the throws-only error-callback reporting guarded above.
        Assert.Contains("__faultTcs.TrySetException(__ex)", csResult);
    }

    #endregion

    #region TryEmit: return-shape coverage

    [Fact]
    public void TryEmit_PrimitiveReturn_EmitsPrimitiveCallbackSig()
    {
        // Int return — primitive shape: callback takes (Int, Int64).
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        // Replace void return with Swift.Int
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.Int"), method.ModuleDecl!);
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        Assert.True(handled);
        var swiftResult = swiftOutput.ToString();
        // Two-arg success callback: (Int, Int64).
        Assert.Contains("@convention(c) (Int, Int64) -> Void", swiftResult);
        Assert.Contains("let _result = await", swiftResult);
        Assert.Contains("callback(_result, _sbwTask)", swiftResult);

        var csResult = csOutput.ToString();
        Assert.Contains("TaskCompletionSource<", csResult);
        Assert.Contains("rawResult", csResult);
    }

    [Fact]
    public void TryEmit_ComplexValueReturn_HeapIndirectViaMarshalFromSwift()
    {
        // Frozen blittable struct return — heap-indirect via UnsafeMutableRawPointer.allocate +
        // initializeMemory on the Swift side. C# value-copies via MarshalFromSwift<T>; the
        // carrier holds no internal refs, so the callback's finally block does a raw free
        // without VWT-Destroy. (Non-frozen struct / complex enum projections take a
        // different branch that copies into a SafeHandle-owned buffer, then destroys the
        // carrier; covered by the runtime AsyncMethodGenericDefaultsTests fixture.)
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithComplexReturn();
        var method = swiftOutput.method;
        var parent = swiftOutput.parent;
        var typeDatabase = swiftOutput.typeDatabase;
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        Assert.True(handled);

        var swiftText = swiftOutput.swiftBuffer.ToString();
        Assert.Contains("UnsafeMutableRawPointer.allocate", swiftText);
        Assert.Contains(
            "initializeMemory(as: TestModule.PurchaseResult.self, repeating: _result, count: 1)",
            swiftText);
        Assert.Contains("callback(_resultBuf, _sbwTask)", swiftText);

        var csText = csOutput.csBuffer.ToString();
        Assert.Contains("MarshalFromSwift<TestModule.PurchaseResult>(rawResult)", csText);
        // Swift-allocated carrier (UnsafeMutableRawPointer.allocate) is freed via the
        // module-scoped SBW_Free helper, not NativeMemory.Free — pairing the matching
        // Swift deallocator avoids the allocator-mismatch bug class fixed in Issue #32.
        Assert.Contains("SBW_Free(rawResult)", csText);
        Assert.DoesNotContain("NativeMemory.Free((void*)rawResult)", csText);
        // SBW_Free P/Invoke is declared per-type, deduped via Utf8SliceEmitter.
        Assert.Contains("private static partial void SBW_Free(IntPtr ptr)", csText);
        // Frozen blittable: no VWT-Destroy of the carrier (no internal refs to release).
        Assert.DoesNotContain("ValueWitnessTable->Destroy((void*)rawResult", csText);
    }

    [Fact]
    public void TryEmit_NonFrozenStructReturn_MarshalThrowReleasesBothCarrierAndCopyBuffer()
    {
        // Non-frozen struct (ClassWithOpaquePayload) → the callback copies the carrier into a
        // fresh SafeHandle-owned buffer, then VWT-Destroys the original carrier. The marshal runs
        // in a try: the carrier's +1 is released in finally so a marshal-throw still balances it,
        // and the catch releases the copy buffer's +1 and frees it so a throw before a SafeHandle
        // adopts __resultBuf cannot orphan that allocation either. Pins the M2 fault-path ordering
        // — a marshal/metadata throw must not leak the carrier OR the copy buffer.
        var (csWriter, swiftWriter, csOutput, swiftOutput) =
            CreateWritersWithComplexReturn(TypeRecordFlags.None);
        var method = swiftOutput.method;
        var parent = swiftOutput.parent;
        var typeDatabase = swiftOutput.typeDatabase;
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        Assert.True(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx));

        var csText = csOutput.csBuffer.ToString();

        // The marshal reads the SafeHandle-owned copy, not the carrier directly.
        Assert.Contains("MarshalFromSwift<TestModule.PurchaseResult>(__resultBuf)", csText);
        Assert.DoesNotContain("MarshalFromSwift<TestModule.PurchaseResult>(rawResult)", csText);

        // Marshal is guarded so a throw cannot skip the releases.
        var tryIdx = csText.IndexOf("result = SwiftMarshal.MarshalFromSwift<TestModule.PurchaseResult>(__resultBuf);", System.StringComparison.Ordinal);
        Assert.True(tryIdx >= 0, "marshal must be emitted inside the guarded block");

        // Catch arm: release the copy buffer's +1 and free it before rethrow.
        var copyDestroyIdx = csText.IndexOf("ValueWitnessTable->Destroy((void*)__resultBuf", System.StringComparison.Ordinal);
        var copyFreeIdx = csText.IndexOf("NativeMemory.Free((void*)__resultBuf", System.StringComparison.Ordinal);
        var rethrowIdx = csText.IndexOf("throw;", System.StringComparison.Ordinal);
        Assert.True(copyDestroyIdx >= 0, "catch must VWT-Destroy the copy buffer's +1");
        Assert.True(copyFreeIdx >= 0, "catch must free the copy buffer allocation");
        Assert.True(rethrowIdx >= 0, "catch must rethrow after releasing the copy buffer");
        Assert.True(copyDestroyIdx < copyFreeIdx && copyFreeIdx < rethrowIdx,
            "catch must Destroy then Free the copy buffer before rethrowing");

        // Finally arm: release the original carrier's +1 (covers the marshal-throw window).
        var carrierDestroyIdx = csText.IndexOf("ValueWitnessTable->Destroy((void*)rawResult", System.StringComparison.Ordinal);
        Assert.True(carrierDestroyIdx >= 0, "finally must VWT-Destroy the carrier's +1");
        // The carrier Destroy must sit in a `finally` that FOLLOWS the guarded marshal — not merely
        // somewhere after it. A linear regression (marshal then `Destroy(rawResult)` on the success
        // line, no try/finally) satisfies a bare `tryIdx < carrierDestroyIdx` ordering yet still
        // leaks the carrier on a marshal-throw; assert a `finally` keyword sits between the marshal
        // and the carrier Destroy (the inner carrier-release finally, not the trailing SBW_Free one).
        var carrierFinallyIdx = csText.IndexOf("finally", tryIdx, System.StringComparison.Ordinal);
        Assert.True(carrierFinallyIdx >= 0 && carrierFinallyIdx < carrierDestroyIdx,
            "carrier Destroy must live in a finally block placed after the guarded marshal");
        Assert.True(rethrowIdx < carrierFinallyIdx,
            "the copy-buffer catch must precede the carrier-release finally");

        // The raw Swift carrier allocation is still reclaimed via SBW_Free below.
        Assert.Contains("SBW_Free(rawResult)", csText);
    }

    [Fact]
    public void TryEmit_FrozenStructProjectedAsClassReturn_MarshalThrowStillReleasesCarrier()
    {
        // Frozen struct with reference-type fields (ClassWithBufferStruct). NewFromPayload runs its
        // own InitializeWithCopy into a managed buffer, so the carrier's +1 must still be released.
        // Resolve the metadata first, marshal in a try, and VWT-Destroy the carrier in finally so a
        // marshal-throw cannot orphan the +1. No copy buffer is allocated on this arm.
        var (csWriter, swiftWriter, csOutput, swiftOutput) = CreateWritersWithComplexReturn(
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        var method = swiftOutput.method;
        var parent = swiftOutput.parent;
        var typeDatabase = swiftOutput.typeDatabase;
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        Assert.True(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx));

        var csText = csOutput.csBuffer.ToString();

        // This arm marshals from the carrier directly — no SafeHandle-owned copy buffer.
        Assert.Contains("MarshalFromSwift<TestModule.PurchaseResult>(rawResult)", csText);
        Assert.DoesNotContain("NativeMemory.Alloc(__resultMetadata.Size)", csText);
        Assert.DoesNotContain("__resultBuf", csText);

        // Marshal is guarded; the carrier's +1 is released in finally after the marshal.
        var marshalIdx = csText.IndexOf("result = SwiftMarshal.MarshalFromSwift<TestModule.PurchaseResult>(rawResult);", System.StringComparison.Ordinal);
        var carrierDestroyIdx = csText.IndexOf("ValueWitnessTable->Destroy((void*)rawResult", System.StringComparison.Ordinal);
        Assert.True(marshalIdx >= 0, "marshal must be emitted inside the guarded block");
        Assert.True(carrierDestroyIdx >= 0, "finally must VWT-Destroy the carrier's +1");
        // As above: a linear regression (marshal then `Destroy(rawResult)` on the success line) also
        // satisfies a bare `marshalIdx < carrierDestroyIdx`. Require a `finally` between the marshal and
        // the carrier Destroy so the Destroy provably sits in the guarded finally, not the success path.
        var carrierFinallyIdx = csText.IndexOf("finally", marshalIdx, System.StringComparison.Ordinal);
        Assert.True(carrierFinallyIdx >= 0 && carrierFinallyIdx < carrierDestroyIdx,
            "carrier Destroy must live in a finally block placed after the guarded marshal");

        // The raw Swift carrier allocation is still reclaimed via SBW_Free below.
        Assert.Contains("SBW_Free(rawResult)", csText);
    }

    #endregion

    #region TryEmit: dedup

    [Fact]
    public void TryEmit_SameSymbolEmittedTwice_OnlyEmitsSwiftOnce()
    {
        // Bridge dispatch may be invoked twice for the same method; the swift wrapper
        // must dedup via ModuleEmissionContext.TryAddMethodWrapperSymbol.
        var (csWriter1, swiftWriter1, _, swiftBuffer1) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter1, swiftWriter1, env, parent, ctx);

        // Second call on a fresh method (same symbol shape via mangled name) must
        // skip the swift emission. Reset the WasEmitted/UsesWrapperLibrary flags
        // (they were set by the first run since it mutates the decl).
        var (csWriter2, swiftWriter2, _, swiftBuffer2) = CreateWritersWithBuffers();
        var method2 = CreateMethodDeclWithGenericParam();
        method2.IsAsync = true;
        var parent2 = CreateClassDecl("Processor");
        method2.ParentDecl = parent2;
        var env2 = new MethodEnvironment(method2, typeDatabase);

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter2, swiftWriter2, env2, parent2, ctx);

        // Second call's swift output must be empty (dedup'd).
        Assert.Equal(string.Empty, swiftBuffer2.ToString());
    }

    #endregion

    #region UCO-escape hardening: holder cleanup via runtime helper (S2 round-3)

    [Fact]
    public void TryEmit_UserParamNamedI_NoCS0136_BecauseCleanupIsHelperCall()
    {
        // Holder cleanup is delegated to the typed holder's instance Cleanup(), so the public
        // ...Async method body no longer inlines a cleanup `for` loop. A Swift parameter projected
        // to `i` therefore cannot shadow a loop index — there is no inlined index to collide with —
        // so CS0136 is structurally impossible without any per-method name reservation. The user
        // parameter must still survive verbatim in the public signature.
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithExtraPrimitiveParam("i");
        var parent = (ClassDecl)method.ParentDecl!;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        Assert.True(AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx));

        var csResult = csOutput.ToString();
        var sigLine = csResult.Split('\n').First(l => l.Contains("ProcessAsync("));
        // The user parameter `i` survives verbatim in the public signature (not dropped/renamed).
        Assert.Contains(" i,", sigLine);
        // The public method frees the holder via the typed holder's instance Cleanup()...
        Assert.Contains("_asyncCallHolder.Cleanup();", csResult);
        // ...and emits no inlined holder-cleanup loop over the holder array (which is what would
        // have shadowed the `i` parameter and required the old SyntheticNameScope rename).
        Assert.DoesNotContain("< _asyncCallHolder.Length;", csResult);
        Assert.DoesNotContain("for (int __i = 1;", csResult);
    }

    [Fact]
    public void TryEmit_HolderCleanup_EmittedAsRuntimeHelperCallInBothPublicAndCallbackScopes()
    {
        // The extraction wires the runtime helper through both site classes: the public ...Async
        // method body (holder var `_asyncCallHolder`) and the [UnmanagedCallersOnly] callbacks
        // (holder var `holder` / `__holder`). None of them inline the slot-walk loop anymore.
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        // Public-method scope (pre-cancel + launch catch) and callback scope both delegate.
        Assert.Contains("_asyncCallHolder.Cleanup();", csResult);
        Assert.Contains("holder.Cleanup();", csResult);
        // The inline slot-walk loop is gone from every scope (no RetainedSelfPtr pattern inlined).
        Assert.DoesNotContain("is RetainedSelfPtr retained", csResult);
    }

    [Fact]
    public void TryEmit_AsyncUCOFaultCatch_RunsHolderCleanupBeforeFaultingTcs()
    {
        // The UCO fault catch (EmitAsyncCallbackFaultCatch) is reachable from result marshalling
        // BEFORE the success path's holder cleanup runs, so the catch must free the holder's native
        // resources itself before faulting the TCS — otherwise retained self / copy buffers /
        // existential heap / deferred containers / cancellation registrations leak whenever
        // marshalling throws. Cleanup is now the exception-safe, idempotent runtime helper call
        // (so re-running it after a partially-completed success path cannot double-free, and a
        // throwing release cannot escape the [UnmanagedCallersOnly] callback into native Swift).
        var (csWriter, swiftWriter, csOutput, _) = CreateWritersWithBuffers();
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        AsyncMethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        var faultBindIdx = csResult.IndexOf("__holder.Tcs is TaskCompletionSource", System.StringComparison.Ordinal);
        var faultSetIdx = csResult.IndexOf("__faultTcs.TrySetException(__ex)", System.StringComparison.Ordinal);
        Assert.True(faultBindIdx >= 0, "UCO fault catch holder bind not found");
        Assert.True(faultSetIdx > faultBindIdx, "TrySetException must follow the holder bind");
        var faultBlock = csResult.Substring(faultBindIdx, faultSetIdx - faultBindIdx);
        // Holder cleanup runs inside the catch (via the typed holder's Cleanup()), before the fault is set.
        Assert.Contains("__holder.Cleanup();", faultBlock);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter);
    }

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput, StringWriter swiftOutput) CreateWritersWithBuffers()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter, csOutput, swiftOutput);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
    }

    private static MethodDecl CreateMethodDecl(string name, bool isConstructor = false)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates an async method with a single class-bound method-own generic parameter
    /// constrained to Describable. Pattern:
    ///     func process&lt;T: Describable &amp; AnyObject&gt;(_ value: T) async
    /// Note: "AnyObject" is modelled as a Swift.AnyObject conformance so the bridge
    /// recognizes it as class-bound.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithGenericParam()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("Processor");
        return new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9Processor7processyyxYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl), // void return
                new ArgumentDecl
                {
                    Name = "_",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Variant of <see cref="CreateMethodDeclWithGenericParam"/> with an extra non-generic
    /// primitive (Swift.Int) parameter whose projected C# name is <paramref name="userParamName"/>.
    /// Used to exercise holder-cleanup loop-index collisions (e.g. a Swift parameter named `i`).
    /// </summary>
    private static MethodDecl CreateMethodDeclWithExtraPrimitiveParam(string userParamName)
    {
        var method = CreateMethodDeclWithGenericParam();
        method.IsAsync = true;
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "_",
            PrivateName = userParamName,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = method.ModuleDecl
        });
        return method;
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
                MetadataAccessor = "$s10TestModule9ProcessorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        var db = CreateTypeDatabase();
        db.AsyncLibraryName = "TestBindings";
        return new MethodEnvironment(method, db);
    }

    /// <summary>
    /// Build a method that returns a complex value type (PurchaseResult). Mirrors the
    /// StoreKit2 shape from the BindingTests fixture. <paramref name="returnTypeFlags"/>
    /// selects which carrier-ownership arm the callback emits: the default
    /// <see cref="TypeRecordFlags.Frozen"/> is a frozen blittable struct (plain arm, raw free,
    /// no VWT-Destroy); a bare struct (no Frozen) is the non-frozen / callback-owned arm
    /// (copy-into-SafeHandle-buffer + carrier Destroy); Frozen|RequiresMemoryManagement is a
    /// frozen-struct-projected-as-class (carrier-needs-destroy arm).
    /// </summary>
    private static (CSharpWriter csWriter, SwiftWriter swiftWriter,
        ComplexEmitOutputs csOutput, ComplexEmitOutputs swiftOutput) CreateWritersWithComplexReturn(
        TypeRecordFlags returnTypeFlags = TypeRecordFlags.Frozen)
    {
        var csBuffer = new StringWriter();
        var swiftBuffer = new StringWriter();
        var csWriter = new CSharpWriter(csBuffer);
        var swiftWriter = new SwiftWriter(swiftBuffer);

        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("Processor");
        var purchaseResultName = SwiftTypeName.FromModuleQualifiedName("TestModule.PurchaseResult");

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9Processor7process_xxYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                // Return PurchaseResult
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.PurchaseResult"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    Name = "_",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        // Register PurchaseResult as a frozen struct
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(parent.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
            SwiftTypeName = parent.SwiftTypeName,
            MetadataAccessor = "$s10TestModule9ProcessorCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });
        testModule.RegisterType(purchaseResultName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PurchaseResult"),
            SwiftTypeName = purchaseResultName,
            MetadataAccessor = "$s10TestModule15PurchaseResultVMa",
            Flags = returnTypeFlags,
            Kind = TypeRecordKind.Struct
        });
        // Replace in DB
        typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(testModule);
        typeDatabase.AsyncLibraryName = "TestBindings";

        var output = new ComplexEmitOutputs
        {
            method = method,
            parent = parent,
            typeDatabase = typeDatabase,
            csBuffer = csBuffer,
            swiftBuffer = swiftBuffer,
        };
        return (csWriter, swiftWriter, output, output);
    }

    private class ComplexEmitOutputs
    {
        public MethodDecl method = null!;
        public TypeDecl parent = null!;
        public TypeDatabase typeDatabase = null!;
        public StringWriter csBuffer = null!;
        public StringWriter swiftBuffer = null!;
    }

    #endregion
}
